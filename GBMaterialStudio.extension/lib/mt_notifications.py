import base64
import os
import subprocess
from xml.sax.saxutils import escape

APP_ID = u'Gest\u00e3o BIM'


def toast(message, title=None, click=None, actions=None):
    icon = os.path.join(os.path.dirname(__file__), 'gestaobim.png')
    if not os.path.isfile(icon):
        raise IOError('Icone nao encontrado: {0}'.format(icon))
    if click or actions:
        raise ValueError('Acoes externas nao sao suportadas neste aviso.')
    xml = u'<toast><visual><binding template="ToastGeneric"><text>{0}</text><text>{1}</text><image placement="appLogoOverride" src="{2}"/></binding></visual></toast>'.format(
        escape(title or APP_ID), escape(message), escape(icon, {'"': '&quot;'}))
    script = u'''$ErrorActionPreference='Stop'
$key = 'HKCU:\\Software\\Classes\\AppUserModelId\\GestaoBIM.Materials'
if ((Get-ItemProperty -Path $key -Name DisplayName -ErrorAction SilentlyContinue).DisplayName -ne '{1}') {{
    New-Item -Path $key -Force | Out-Null
    New-ItemProperty -Path $key -Name DisplayName -Value '{1}' -PropertyType String -Force | Out-Null
}}
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType=WindowsRuntime] | Out-Null
$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml('{0}')
$toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('GestaoBIM.Materials').Show($toast)
'''.format(xml.replace("'", "''"), APP_ID.replace("'", "''"))
    encoded = base64.b64encode(script.encode('utf-16le'))
    command = ['powershell.exe', '-NoProfile', '-NonInteractive', '-WindowStyle',
               'Hidden', '-EncodedCommand', encoded.decode('ascii')]
    return subprocess.Popen(command, shell=False, creationflags=0x08000000)
