# -*- coding: utf-8 -*-
"""WPF helpers for CPython. pyrevit.forms is not available under CPython, so windows are loaded here."""
import uuid

import clr
clr.AddReference('PresentationFramework')
clr.AddReference('PresentationCore')
clr.AddReference('WindowsBase')

import System
from System.Collections.Generic import List
from System.IO import FileAccess, FileMode, FileShare, FileStream
from System.Windows.Interop import WindowInteropHelper
from System.Windows.Markup import XamlReader

# Python classes that WPF binds to are created as .NET types; every load of the
# extension uses a fresh namespace so reloading never collides with an old type.
NAMESPACE = 'GB' + uuid.uuid4().hex[:10]


def clr_property(clr_type, attribute, default=None):
    """A real .NET property (needed by WPF for non-text bindings such as brushes).

    The value is read from the Python attribute `attribute` on every access, so
    ItemsControl.Items.Refresh() shows later changes.
    """
    def getter(self):
        value = getattr(self, attribute, default)
        return default if value is None else value
    return clr.clrproperty(clr_type)(getter)


class Window(object):
    """A WPF window loaded from XAML. Named controls are attributes: window.BtnClose."""

    def __init__(self, xaml_path):
        stream = FileStream(xaml_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        try:
            native = XamlReader.Load(stream)
        finally:
            stream.Close()
        object.__setattr__(self, 'native', native)

    @classmethod
    def from_string(cls, xaml):
        instance = cls.__new__(cls)
        object.__setattr__(instance, 'native', XamlReader.Parse(xaml))
        return instance

    def __getattr__(self, name):
        native = object.__getattribute__(self, 'native')
        control = native.FindName(name)
        return control if control is not None else getattr(native, name)

    def __setattr__(self, name, value):
        setattr(self.native, name, value)


def net_list(items):
    """A .NET list WPF can bind to. A Python list is rejected by ItemsSource."""
    collection = List[System.Object]()
    for item in items:
        collection.Add(item)
    return collection


def native_window(window):
    """The System.Windows.Window behind a Window wrapper, or the window itself."""
    return getattr(window, 'native', window)


def attach_to_revit(window):
    """Parent the window to Revit's main window so it never hides behind it."""
    try:
        helper = WindowInteropHelper(native_window(window))
        helper.Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle
    except Exception:
        pass
