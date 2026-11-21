# -*- coding: utf-8 -*-
"""Lets a WPF window stay open while the user keeps working in Revit.

Revit only accepts API calls inside a valid API context. A modeless window's
event handlers run outside one, so the handlers that touch the model are posted
to an ExternalEvent and run the next time Revit is idle.
"""
import traceback

from Autodesk.Revit.UI import ExternalEvent, IExternalEventHandler
from pyrevit import forms

_HELD = {}
_DISPATCHER = None


class _Dispatcher(IExternalEventHandler):
    def __init__(self):
        self.queue = []
        self.event = ExternalEvent.Create(self)

    def Execute(self, uiapp):
        pending, self.queue = self.queue, []
        for call in pending:
            try:
                call()
            except Exception as error:
                forms.alert('{0}\n\n{1}'.format(error, traceback.format_exc()),
                            title=u'Gestão BIM')

    def GetName(self):
        return 'GBMaterialStudio.Dispatcher'

    def post(self, call):
        self.queue.append(call)
        self.event.Raise()


def dispatcher():
    """The shared dispatcher. Must be first called from a valid API context (a button click)."""
    global _DISPATCHER
    if _DISPATCHER is None:
        _DISPATCHER = _Dispatcher()
    return _DISPATCHER


def in_revit(function):
    """Wrap a window event handler so it runs inside a Revit API context."""
    def wrapper(*args):
        dispatcher().post(lambda: function(*args))
    return wrapper


def hold(window):
    """Keep a modeless window referenced until it closes, and close any previous one."""
    for other in list(_HELD.values()):
        try:
            other.Close()
        except Exception:
            pass
    key = id(window)
    _HELD[key] = window

    def release(sender, args):
        _HELD.pop(key, None)

    window.Closed += release
