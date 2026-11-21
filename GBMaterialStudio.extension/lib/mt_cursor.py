import os
import clr
clr.AddReference('System.Windows.Forms')
from System.Windows.Forms import Timer

_HERE = os.path.dirname(__file__)
_NATIVE = None


def _native():
    global _NATIVE
    if _NATIVE is None:
        try:
            import GestaoBimCursorHook
        except ImportError:
            clr.AddReferenceToFileAndPath(os.path.join(_HERE, 'GestaoBimCursorHook.dll'))
            import GestaoBimCursorHook
        _NATIVE = GestaoBimCursorHook
    return _NATIVE


class EyedropperCursor(object):
    """Shows the eyedropper cursor while the Revit pick runs.

    A WM_SETCURSOR hook reapplies the cursor right after Revit sets its own, so
    it never flickers. The timer only covers cursor changes made outside that
    message. Falls back silently to Revit's cursor if the native part fails.
    """

    def __init__(self):
        self.timer = None
        self.native = None

    def __enter__(self):
        try:
            self.native = _native()
            if not self.native.Install(os.path.join(_HERE, 'eyedropper.cur')):
                self.native = None
                return self
            self.timer = Timer()
            self.timer.Interval = 40
            self.timer.Tick += self._tick
            self.timer.Start()
        except Exception:
            self.native = None
        return self

    def _tick(self, sender, args):
        self.native.Enforce()

    def __exit__(self, exc_type, exc_value, traceback):
        if self.timer is not None:
            self.timer.Stop()
            self.timer.Tick -= self._tick
            self.timer.Dispose()
            self.timer = None
        if self.native is not None:
            self.native.Uninstall()
            self.native = None


def pick_face(uidoc, object_type, selection_filter, prompt):
    with EyedropperCursor():
        return uidoc.Selection.PickObject(object_type, selection_filter, prompt)
