import os
import System
import System.IO
import System.Windows.Media
import System.Windows.Media.Imaging
from pyrevit import UI

import mt_dialogs
import mt_wpf
import mt_favorites
import mt_editor
import mt_services
import System
from mt_uihelpers import attach_to_revit, resolve_texture_path, to_brush


class PreviewItem(System.Object):
    """A favorite in the list. A .NET type, so WPF can bind its brush."""
    __namespace__ = mt_wpf.NAMESPACE

    def __init__(self, item):
        self.data = item.data
        self._display = item.Display
        self._name = item.Name
        self.Source = item.Source
        self.PreviewKind = 'Cor do material'
        self._preview_brush = self._preview()

    Display = mt_wpf.clr_property(System.String, '_display', '')
    Name = mt_wpf.clr_property(System.String, '_name', '')
    PreviewBrush = mt_wpf.clr_property(System.Windows.Media.Brush, '_preview_brush')

    def _preview(self):
        path = (self.data.get('appearance') or {}).get('texture_path', '')
        try:
            resolved = resolve_texture_path(path) if path else None
            if resolved and System.IO.File.Exists(resolved):
                stream = System.IO.FileStream(resolved, System.IO.FileMode.Open,
                    System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite)
                try:
                    bitmap = System.Windows.Media.Imaging.BitmapImage()
                    bitmap.BeginInit()
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad
                    bitmap.DecodePixelWidth = 96
                    bitmap.StreamSource = stream
                    bitmap.EndInit()
                    bitmap.Freeze()
                finally:
                    stream.Close()
                brush = System.Windows.Media.ImageBrush(bitmap)
                brush.Stretch = System.Windows.Media.Stretch.UniformToFill
                brush.Freeze()
                self.PreviewKind = 'Textura do material'
                return brush
        except Exception:
            pass
        if path:
            self.PreviewKind = 'Textura indisponivel - amostra de cor'
        color = (self.data.get('graphics') or {}).get('color') or {}
        values = [max(0, min(255, int(color.get(key, 160)))) for key in ['r','g','b']]
        return to_brush('#{0:02X}{1:02X}{2:02X}'.format(*values))


def filter_items(items, query):
    text = (query or '').strip().lower()
    return [item for item in items if text in item.Display.lower()]


def update_details(window, item):
    enabled = item is not None
    window.Open.IsEnabled = enabled
    window.ApplyFaces.IsEnabled = enabled
    window.DetailsTabs.IsEnabled = enabled
    window.SelectedName.Text = item.Name if enabled else 'Selecione um favorito'
    window.SelectedPreviewKind.Text = item.PreviewKind if enabled else 'A previa e os detalhes aparecem aqui.'
    window.SelectedPreview.Background = item.PreviewBrush if enabled else to_brush('#E8EDEF')
    window.DetailTexturePreview.Background = item.PreviewBrush if enabled else to_brush('#E8EDEF')
    data = item.data if enabled else {}
    identity = data.get('identity') or {}
    appearance = data.get('appearance') or {}
    for name, value in [
        ('DetailName', data.get('name', '')),
        ('DetailClass', identity.get('material_class', '')),
        ('DetailDescription', identity.get('description', '')),
        ('DetailTexturePath', appearance.get('texture_path', '')),
        ('DetailSource', data.get('source_doc', '')),
        ('DetailSourcePath', data.get('source_path', '')),
    ]:
        getattr(window, name).Text = value or ''
    window.DetailTextureStatus.Text = item.PreviewKind if enabled else ''
    if not enabled:
        transfer = ''
    elif data.get('library_uid'):
        transfer = 'Na biblioteca portatil: transfere para qualquer projeto, sem abrir a origem.'
    elif data.get('source_key') or data.get('project_id'):
        transfer = 'Ainda fora da biblioteca. Abra o projeto de origem e reabra os favoritos para copiar.'
    else:
        transfer = 'Registro legado parcial. Favorite novamente no projeto de origem para transferir com seguranca.'
    window.DetailTransferStatus.Text = transfer


def show_favorites(doc, uidoc):
    root = os.path.dirname(__file__)
    window = mt_wpf.Window(os.path.join(root, 'FavoritesWindow.xaml'))
    state = {'items': [], 'material': None, 'paint': False, 'note': ''}
    try:
        copied, errors = mt_favorites.sync_to_library()
        if copied:
            state['note'] = '{0} favorito(s) copiado(s) para a biblioteca portatil.'.format(copied)
        if errors:
            state['note'] = 'Biblioteca: {0}'.format(errors[0])
    except Exception as error:
        state['note'] = 'Biblioteca portatil indisponivel: {0}'.format(error)

    def search(sender=None, args=None):
        visible = filter_items(state['items'], window.Search.Text)
        window.Favorites.ItemsSource = mt_wpf.net_list(visible)
        if not state['items']:
            window.Status.Text = 'Sua biblioteca esta vazia. Abra um material e clique em Favoritar para salva-lo aqui.'
        elif not visible:
            window.Status.Text = 'Nenhum material corresponde a busca. Limpe o campo para ver todos os favoritos.'
        else:
            window.Status.Text = state['note'] or '{0} favorito(s). Itens "na biblioteca" transferem sem abrir a origem.'.format(len(visible))
        update_details(window, None)

    def refresh(sender=None, args=None):
        try:
            state['items'] = [PreviewItem(item) for item in mt_favorites.list_favorites()]
            search()
        except Exception as error:
            state['items'] = []
            window.Favorites.ItemsSource = mt_wpf.net_list([])
            update_details(window, None)
            window.Status.Text = 'Nao foi possivel ler a biblioteca. Nenhum dado foi alterado: {0}'.format(error)

    def selection(sender, args):
        update_details(window, window.Favorites.SelectedItem)

    def load_material(paint=False):
        item = window.Favorites.SelectedItem
        if item is None:
            return
        try:
            state['material'] = mt_favorites.get_or_create_favorite_material(doc, item.data)
            if state['material'] is None:
                raise ValueError('Material nao encontrado.')
            state['paint'] = paint
            window.Close()
        except Exception as error:
            mt_dialogs.alert(str(error), title=u'Gest\u00e3o BIM | Favoritos')

    def open_material(sender, args):
        load_material(False)

    def apply_faces(sender, args):
        load_material(True)

    def close(sender, args):
        window.Close()

    window.Search.TextChanged += search
    window.Favorites.SelectionChanged += selection
    window.Refresh.Click += refresh
    window.Open.Click += open_material
    window.ApplyFaces.Click += apply_faces
    window.CloseButton.Click += close
    refresh()
    attach_to_revit(window)
    window.ShowDialog()
    if state['material'] is not None:
        if state['paint']:
            mt_services.paint_material_on_faces(doc, uidoc, state['material'],
                paint_label=state['material'].Name)
            return
        outcome = mt_editor.show_material_editor(doc, uidoc, state['material'],
            os.path.join(root, 'MaterialEditorWindow.xaml'))
        if outcome and outcome.get('open_native'):
            command = UI.RevitCommandId.LookupPostableCommandId(UI.PostableCommand.Materials)
            if command is not None:
                from pyrevit import HOST_APP
                HOST_APP.uiapp.PostCommand(command)
