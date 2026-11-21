# -*- coding: utf-8 -*-
"""Message boxes and pickers for CPython scripts, where pyrevit.forms is not available."""
import clr
clr.AddReference('PresentationFramework')
clr.AddReference('PresentationCore')
clr.AddReference('WindowsBase')
clr.AddReference('System.Windows.Forms')

import System
from System.Collections.Generic import List
from System.Windows import MessageBox, MessageBoxButton, MessageBoxImage, MessageBoxResult
from System.Windows.Forms import ColorDialog, DialogResult
from Microsoft.Win32 import OpenFileDialog

import mt_wpf

TITLE = u'Gestão BIM'

_DIALOG = u'''
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="{title}" SizeToContent="WidthAndHeight" MinWidth="420" ResizeMode="NoResize" ShowInTaskbar="False"
        WindowStartupLocation="CenterScreen" FontFamily="Segoe UI" FontSize="13" Background="#F4F6F7">
  <StackPanel Margin="18">
    <TextBlock x:Name="Prompt" TextWrapping="Wrap" MaxWidth="520" Foreground="#20343C" Margin="0,0,0,10"/>
    {body}
    <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,14,0,0">
      <Button x:Name="BtnOk" Content="{ok}" IsDefault="True" MinWidth="96" Padding="12,7" Margin="0,0,8,0"/>
      <Button x:Name="BtnCancel" Content="Cancelar" IsCancel="True" MinWidth="96" Padding="12,7"/>
    </StackPanel>
  </StackPanel>
</Window>'''

_TEXT_BODY = u'<TextBox x:Name="Input" MinHeight="32" Padding="8,5" VerticalContentAlignment="Center"/>'

_LIST_BODY = u'''<TextBox x:Name="Filter" MinHeight="32" Padding="8,5" VerticalContentAlignment="Center" Margin="0,0,0,8"
                 ToolTip="Digite para filtrar a lista"/>
    <ListBox x:Name="Items" Height="360" MinWidth="620" SelectionMode="{mode}"/>'''


def _escape(text):
    return (text or u'').replace(u'&', u'&amp;').replace(u'<', u'&lt;').replace(u'>', u'&gt;').replace(u'"', u'&quot;')


def _build(title, prompt, body, ok_text):
    xaml = _DIALOG.replace(u'{title}', _escape(title)).replace(u'{body}', body).replace(u'{ok}', _escape(ok_text))
    window = mt_wpf.Window.from_string(xaml)
    window.Prompt.Text = prompt or u''
    window.BtnOk.Click += lambda sender, args: setattr(window, 'DialogResult', True)
    mt_wpf.attach_to_revit(window)
    return window


def alert(message, title=TITLE, yes=False, no=False):
    """Show a message. With yes/no it asks and returns True only for Yes; otherwise it returns True."""
    if yes or no:
        answer = MessageBox.Show(u'{0}'.format(message), title, MessageBoxButton.YesNo, MessageBoxImage.Question)
        return answer == MessageBoxResult.Yes
    MessageBox.Show(u'{0}'.format(message), title, MessageBoxButton.OK, MessageBoxImage.Information)
    return True


def ask_for_string(default=u'', prompt=u'', title=TITLE):
    """Ask for a line of text. Returns None when cancelled."""
    window = _build(title, prompt, _TEXT_BODY, u'OK')
    window.Input.Text = default or u''
    window.Input.SelectAll()
    window.Input.Focus()
    return window.Input.Text if window.ShowDialog() else None


def ask_for_color():
    """Ask for a colour. Returns '#RRGGBB' or None when cancelled."""
    dialog = ColorDialog()
    dialog.FullOpen = True
    if dialog.ShowDialog() != DialogResult.OK:
        return None
    color = dialog.Color
    return u'#{0:02X}{1:02X}{2:02X}'.format(color.R, color.G, color.B)


def pick_file(file_ext=u'', title=u'Selecionar arquivo'):
    """Pick a file. file_ext is a ';'-separated list such as 'jpg;png'. Returns the path or None."""
    dialog = OpenFileDialog()
    dialog.Title = title
    extensions = [e.strip().lstrip(u'.') for e in (file_ext or u'').split(u';') if e.strip()]
    if extensions:
        pattern = u';'.join(u'*.{0}'.format(e) for e in extensions)
        dialog.Filter = u'Arquivos ({0})|{0}|Todos os arquivos|*.*'.format(pattern)
    return dialog.FileName if dialog.ShowDialog() else None


def select_from_list(items, title=TITLE, multiselect=False, button_name=u'Selecionar'):
    """Pick from a list of strings with a filter box.

    Returns a list (multiselect) or one string, or None when cancelled or nothing is chosen.
    Items hidden by the filter stay selected.
    """
    items = list(items)
    mode = u'Extended' if multiselect else u'Single'
    window = _build(title, u'', _LIST_BODY.replace(u'{mode}', mode), button_name)
    window.Prompt.Visibility = System.Windows.Visibility.Collapsed
    chosen = set()
    state = {'refreshing': False}

    def show(visible):
        state['refreshing'] = True
        try:
            collection = List[System.String]()
            for item in visible:
                collection.Add(item)
            window.Items.ItemsSource = collection
            for item in visible:
                if item in chosen:
                    window.Items.SelectedItems.Add(item)
        finally:
            state['refreshing'] = False

    def on_filter(sender, args):
        text = (window.Filter.Text or u'').strip().lower()
        show([i for i in items if text in i.lower()])

    def on_selection(sender, args):
        if state['refreshing']:
            return
        for item in args.RemovedItems:
            chosen.discard(item)
        for item in args.AddedItems:
            chosen.add(item)

    window.Filter.TextChanged += on_filter
    window.Items.SelectionChanged += on_selection
    window.Items.MouseDoubleClick += lambda sender, args: (
        setattr(window, 'DialogResult', True) if chosen and not multiselect else None)
    show(items)
    window.Filter.Focus()
    if not window.ShowDialog() or not chosen:
        return None
    picked = [i for i in items if i in chosen]
    return picked if multiselect else picked[0]
