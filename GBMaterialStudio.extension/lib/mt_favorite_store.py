# -*- coding: utf-8 -*-
"""Fail-closed JSON favorites persistence, without Revit dependencies.

Only a missing file invokes default_factory. Invalid or unreadable databases raise
and are never silently replaced. Backups are retained beside the original file.
Inside Revit the writers coordinate through a named .NET mutex, which Windows releases if
a writer crashes. Without .NET (the test suite) a cooperative lock file is used; it
deliberately never reclaims stale locks.
"""
import errno
import hashlib
import json
import os
import tempfile
import time
import uuid
from datetime import datetime, timezone


LOCK_TIMEOUT_SECONDS = 10.0
LOCK_POLL_SECONDS = 0.02
_INTEGER_TYPES = (int,)


def _has_dotnet():
    try:
        import clr  # noqa: F401
        from System.Threading import Mutex  # noqa: F401
    except ImportError:
        return False
    return True


class StoreError(ValueError):
    """The database does not satisfy the supported schema."""


class LockTimeoutError(IOError):
    """Another writer held the lock longer than the bounded wait."""


def _validate(db):
    if not isinstance(db, dict):
        raise StoreError('Favorites database must be an object')
    if 'favorites' not in db or not isinstance(db['favorites'], list):
        raise StoreError('favorites must be a list')
    if any(not isinstance(item, dict) for item in db['favorites']):
        raise StoreError('Each favorite must be an object')
    if 'version' in db:
        version = db['version']
        if (isinstance(version, bool) or
                not isinstance(version, _INTEGER_TYPES) or version < 0):
            raise StoreError('version must be a nonnegative integer')
        if version > 4:
            raise StoreError('Database version is newer than supported version 4')
    return db


def _reject_constant(value):
    raise StoreError('Non-finite JSON number: ' + value)


def _decode(raw):
    try:
        db = json.loads(raw.decode('utf-8-sig'), parse_constant=_reject_constant)
    except (ValueError, UnicodeError) as error:
        raise StoreError('Invalid favorites JSON: ' + str(error))
    return _validate(db)


def _read(path, default_factory):
    try:
        with open(path, 'rb') as stream:
            raw = stream.read()
    except IOError as error:
        if error.errno != errno.ENOENT:
            raise
        return _validate(default_factory()), None
    return _decode(raw), raw


def load(path, default_factory):
    """Read one atomic snapshot. Corruption and future versions raise StoreError."""
    return _read(os.path.abspath(path), default_factory)[0]


def _clock():
    return getattr(time, 'monotonic', time.time)()


class _FileLock(object):
    def __init__(self, path):
        self.path = path + '.lock'
        self.fd = None
        self.token = uuid.uuid4().hex.encode('ascii')

    def __enter__(self):
        deadline = _clock() + LOCK_TIMEOUT_SECONDS
        while True:
            try:
                self.fd = os.open(self.path, os.O_CREAT | os.O_EXCL | os.O_WRONLY,
                                  0o600)
                break
            except OSError as error:
                if error.errno not in (errno.EEXIST, errno.EACCES):
                    raise
                remaining = deadline - _clock()
                if remaining <= 0:
                    raise LockTimeoutError('Timed out waiting for favorites lock')
                time.sleep(min(LOCK_POLL_SECONDS, remaining))
        try:
            os.write(self.fd, self.token)
        except BaseException:
            self.__exit__(None, None, None)
            raise
        return self

    def __exit__(self, exc_type, exc_value, traceback):
        # Never remove another owner's lock, including on timeout. No stale-lock
        # stealing: an operator must inspect and remove a crashed writer's lock.
        if self.fd is not None:
            try:
                identity = os.fstat(self.fd)
                with open(self.path, 'rb') as stream:
                    current = stream.read()
                    current_identity = os.fstat(stream.fileno())
                if (current == self.token and
                        identity.st_ino == current_identity.st_ino and
                        identity.st_dev == current_identity.st_dev):
                    os.close(self.fd)
                    self.fd = None
                    os.unlink(self.path)
            except (IOError, OSError):
                pass
            finally:
                if self.fd is not None:
                    os.close(self.fd)
                    self.fd = None


class _MutexLock(object):
    def __init__(self, path):
        from System.Threading import Mutex
        normalized = os.path.normcase(os.path.abspath(path))
        digest = hashlib.sha256(normalized.encode('utf-8')).hexdigest()
        self.mutex = Mutex(False, 'Local\\ValberFavoriteStore_' + digest)
        self.acquired = False

    def __enter__(self):
        from System.Threading import AbandonedMutexException
        try:
            try:
                self.acquired = self.mutex.WaitOne(int(LOCK_TIMEOUT_SECONDS * 1000))
            except AbandonedMutexException:
                # .NET grants ownership when reporting abandonment. Reading and
                # validation below still fail closed if the last writer crashed.
                self.acquired = True
            if not self.acquired:
                raise LockTimeoutError('Timed out waiting for favorites mutex')
        except BaseException:
            self.mutex.Dispose()
            raise
        return self

    def __exit__(self, exc_type, exc_value, traceback):
        try:
            if self.acquired:
                self.mutex.ReleaseMutex()
        finally:
            self.mutex.Dispose()


def _flush(stream):
    stream.flush()
    if hasattr(os, 'fsync'):
        os.fsync(stream.fileno())


def _backup(path, raw):
    stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    backup = path + '.bak.' + stamp + '.' + uuid.uuid4().hex
    fd = os.open(backup, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, 'wb') as stream:
        stream.write(raw)
        _flush(stream)
    return backup


def _atomic_replace(source, target, exists):
    # No delete-then-rename fallback: lack of atomic replace must fail closed.
    os.replace(source, target)


def mutate(path, default_factory, callback):
    """Lock, read, call callback(db), validate, back up and atomically publish.

    Returns callback's result. A callback or serialization failure never writes.
    The containing directory must already exist. All writers must use this API.
    """
    path = os.path.abspath(path)
    lock_type = _MutexLock if _has_dotnet() else _FileLock
    with lock_type(path):
        db, original = _read(path, default_factory)
        result = callback(db)
        _validate(db)
        payload = json.dumps(db, ensure_ascii=False, allow_nan=False,
                             indent=2).encode('utf-8')
        # Validate the serialized representation too (e.g. callbacks can supply
        # tuples/custom objects that do not round-trip as the supported schema).
        _decode(payload)
        fd, temporary = tempfile.mkstemp(prefix='.favorites-', suffix='.tmp',
                                         dir=os.path.dirname(path))
        try:
            with os.fdopen(fd, 'wb') as stream:
                stream.write(payload)
                _flush(stream)
            if original is not None:
                _backup(path, original)
            _atomic_replace(temporary, path, original is not None)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)
        return result
