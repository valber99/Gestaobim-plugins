import os
import uuid

from pyrevit import DB, HOST_APP
import mt_favorites_legacy as legacy
import mt_favorite_store as store
import mt_hygiene
import mt_library

try:
    string_types = (basestring,)
except NameError:
    string_types = (str,)


class FavoriteItem(legacy.FavoriteItem):
    @property
    def Display(self):
        if self.data.get('library_uid'):
            status = 'na biblioteca'
        elif self.data.get('source_key') or self.data.get('project_id'):
            status = 'origem nativa'
        else:
            status = 'legado parcial'
        return u'{0} | {1} [{2}]'.format(self.Name, self.Source, status)


LEGACY_DB_PATH = legacy.DB_PATH
DB_PATH = os.path.join(legacy.APPDATA_DIR, 'favorites-v4.json')
APPDATA_DIR = legacy.APPDATA_DIR


def _default_db():
    if os.path.isfile(LEGACY_DB_PATH):
        return store.load(LEGACY_DB_PATH, lambda: {'version': 3, 'favorites': []})
    return {'version': 4, 'mode': 'native-reference', 'favorites': []}


def load_db():
    return store.load(DB_PATH, _default_db)


def _project_id(doc):
    info = getattr(doc, 'ProjectInformation', None)
    if not info:
        raise ValueError('Favoritos portaveis exigem um documento de projeto, nao uma familia.')
    return info.UniqueId


def _legacy_source_key(doc):
    return u'{0}|{1}'.format(_project_id(doc), os.path.normcase(doc.PathName or ''))


def _from_doc(item, doc):
    """True when the favorite was saved from this project.

    New favorites match by the project's internal id, so moving or renaming the
    file keeps them. Older ones only carry the id plus file path.
    """
    project_id = item.get('project_id')
    if project_id:
        return project_id == _project_id(doc)
    return item.get('source_key') == _legacy_source_key(doc)


def _same_source(item, doc, material):
    return _from_doc(item, doc) and item.get('material_unique_id') == material.UniqueId


def _parameter(material, bip):
    parameter = material.get_Parameter(bip)
    return parameter.AsString() if parameter else ''


def serialize_material(doc, material):
    data = legacy.serialize_material(doc, material)
    for key, bip in [('description', DB.BuiltInParameter.ALL_MODEL_DESCRIPTION),
                     ('manufacturer', DB.BuiltInParameter.ALL_MODEL_MANUFACTURER),
                     ('model', DB.BuiltInParameter.ALL_MODEL_MODEL),
                     ('url', DB.BuiltInParameter.ALL_MODEL_URL),
                     ('mark', DB.BuiltInParameter.ALL_MODEL_MARK)]:
        data['identity'][key] = legacy._safe_text(_parameter(material, bip))
    data['id'] = str(uuid.uuid4())
    data['project_id'] = _project_id(doc)
    data['source_key'] = _legacy_source_key(doc)
    data['source_path'] = doc.PathName or ''
    data['material_unique_id'] = material.UniqueId
    data['signature'] = mt_hygiene.signature_hash(doc, material)
    data['revit_version'] = doc.Application.VersionNumber
    data['transfer_mode'] = 'native-open-source'
    data['snapshot_only'] = True
    return data


def list_favorites(db=None):
    db = db if db is not None else load_db()
    return sorted([FavoriteItem(x) for x in db['favorites']],
                  key=lambda item: item.Name.lower())


def is_favorite(material, db=None):
    db = db if db is not None else load_db()
    doc = material.Document
    return any(_same_source(item, doc, material) for item in db['favorites'])


def add_or_update_favorite(doc, material, to_library=True):
    """Save the material as a favorite and copy it into the portable library.

    The favorite is saved even when the library copy fails; the reasons are in
    the returned item's `warnings`.
    """
    data = serialize_material(doc, material)
    previous = None
    for old in load_db()['favorites']:
        if _same_source(old, doc, material):
            previous = old
            break
    warnings = []
    if to_library:
        try:
            data['library_uid'] = mt_library.push_material(
                doc, material, previous.get('library_uid') if previous else None)
        except Exception as error:
            warnings.append('Biblioteca portatil nao atualizada: {0}'.format(error))
    directory = os.path.dirname(DB_PATH)
    if not os.path.isdir(directory):
        try:
            os.makedirs(directory)
        except OSError:
            if not os.path.isdir(directory):
                raise

    def update(db):
        for index, old in enumerate(db['favorites']):
            if _same_source(old, doc, material):
                data['id'] = old['id']
                merged = dict(old)
                merged.update(data)
                db['favorites'][index] = merged
                break
        else:
            db['favorites'].append(data)
        db['version'] = 4
        db['mode'] = 'native-reference'
        return FavoriteItem(data)

    item = store.mutate(DB_PATH, _default_db, update)
    item.warnings = warnings
    return item


def remove_favorite(material_or_id):
    """Remove a favorite and its library copy. Returns a list of warnings."""
    removed_uids = []

    def remove(db):
        if isinstance(material_or_id, string_types):
            matches = [x for x in db['favorites'] if x.get('id') == material_or_id]
        else:
            doc = material_or_id.Document
            matches = [x for x in db['favorites'] if _same_source(x, doc, material_or_id)]
        if len(matches) > 1:
            raise ValueError('Identificador ambiguo. Nenhum favorito foi removido.')
        if matches:
            db['favorites'].remove(matches[0])
            if matches[0].get('library_uid'):
                removed_uids.append(matches[0]['library_uid'])
        still_used = set(x.get('library_uid') for x in db['favorites'])
        removed_uids[:] = [uid for uid in removed_uids if uid not in still_used]

    store.mutate(DB_PATH, _default_db, remove)
    warnings = []
    if removed_uids:
        try:
            mt_library.remove_materials(removed_uids)
        except Exception as error:
            warnings.append('A copia na biblioteca nao foi removida: {0}'.format(error))
    return warnings


def import_favorite_to_doc(doc, favorite_data):
    if _from_doc(favorite_data, doc):
        uid = favorite_data.get('material_unique_id')
        return doc.GetElement(uid) if uid else None
    return None


def _source_document(app, data):
    for candidate in app.Documents:
        if candidate.IsLinked or candidate.IsFamilyDocument:
            continue
        if _from_doc(data, candidate):
            return candidate
    return None


def _find_identical(doc, favorite_data):
    """A material already in doc that is the same as the favorite (e.g. transferred earlier)."""
    signature = favorite_data.get('signature')
    name = (favorite_data.get('name') or '').lower()
    if not signature or not name:
        return None
    for candidate in DB.FilteredElementCollector(doc).OfClass(DB.Material):
        if candidate.Name.lower() == name and mt_hygiene.signature_hash(doc, candidate) == signature:
            return candidate
    return None


def get_or_create_favorite_material(doc, favorite_data):
    existing = import_favorite_to_doc(doc, favorite_data)
    if existing:
        return existing
    has_origin = favorite_data.get('source_key') or favorite_data.get('project_id')
    if not has_origin and not favorite_data.get('library_uid'):
        raise ValueError('Favorito antigo possui apenas snapshot parcial. Abra o projeto de origem e favorite novamente. O registro antigo foi preservado.')
    identical = _find_identical(doc, favorite_data)
    if identical is not None:
        return identical
    source = _source_document(HOST_APP.app, favorite_data) if has_origin else None
    if source is not None:
        material = source.GetElement(favorite_data.get('material_unique_id'))
        if material:
            return mt_library.transfer_material(source, material, doc)
    library_uid = favorite_data.get('library_uid')
    if library_uid:
        return mt_library.pull_material(doc, library_uid)
    raise ValueError('Abra o projeto de origem no mesmo Revit para transferir o material completo, ou favorite o material novamente para guardar uma copia na biblioteca. Origem: {0}. Nenhuma copia simplificada foi criada.'.format(favorite_data.get('source_path') or favorite_data.get('source_doc', '')))


def sync_to_library():
    """Copy favorites that have no library copy yet, when their source project is open.

    Returns (copied_count, error_messages).
    """
    app = HOST_APP.app
    pending = [x for x in load_db()['favorites']
               if not x.get('library_uid') and (x.get('project_id') or x.get('source_key'))]
    pushed = {}
    errors = []
    for item in pending:
        source = _source_document(app, item)
        if source is None:
            continue
        material = source.GetElement(item.get('material_unique_id'))
        if material is None:
            continue
        try:
            pushed[item['id']] = mt_library.push_material(source, material, None, app)
        except Exception as error:
            errors.append(u'{0}: {1}'.format(item.get('name', ''), error))
    if pushed:
        def apply(db):
            for favorite in db['favorites']:
                if favorite.get('id') in pushed:
                    favorite['library_uid'] = pushed[favorite['id']]
        store.mutate(DB_PATH, _default_db, apply)
    return len(pushed), errors


def create_material_from_favorite(doc, favorite_data):
    return get_or_create_favorite_material(doc, favorite_data)


def apply_favorite_to_material(doc, target_material, favorite_data):
    raise ValueError('Sobrescrita por snapshot desativada para evitar perda. Use transferencia nativa para criar um material independente.')
