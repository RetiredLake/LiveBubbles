"""Ensure this notification change did not alter the pre-existing multimedia stack."""
from pathlib import Path
import re
import subprocess

root = Path(__file__).resolve().parents[2]
baseline = 'b51f46d'
def old(path):
    return subprocess.check_output(['git', 'show', baseline + ':' + path], cwd=root).decode('utf-8-sig').replace('\r\n','\n')
def current(path):
    return (root / path).read_text(encoding='utf-8-sig')
def normalize_branding(path, source):
    # Product-label changes are allowed in protected media/client methods. This
    # keeps the audit focused on behavior while still rejecting logic changes.
    if path == 'src/WpBlueBubbles/Services/BlueBubblesClient.cs':
        source = source.replace('LiveBubbles could not stop the typing indicator.', 'BlueBubbles could not stop the typing indicator.')
        source = source.replace('The LiveBubbles typing connection closed unexpectedly.', 'The BlueBubbles typing connection closed unexpectedly.')
        source = source.replace('LiveBubbles did not confirm that typing stopped.', 'BlueBubbles did not confirm that typing stopped.')
    if path == 'src/WpBlueBubbles/MainPage.xaml.cs':
        source = source.replace('new MessageDialog("Saved to Pictures.", "LiveBubbles")', 'new MessageDialog("Saved to Pictures.", "BlueBubbles")')
        source = source.replace('fileName = "LiveBubbles-"', 'fileName = "BlueBubbles-"')
        source = source.replace('LiveBubbles could not read the shared item:', 'BlueBubbles could not read the shared item:')
    return source
for file in ['src/WpBlueBubbles/Models/MessageItem.cs', 'src/WpBlueBubbles/Services/BlueBubblesClient.cs']:
    assert normalize_branding(file, old(file)) == normalize_branding(file, current(file)), file + ' changed'
    print('UNCHANGED', file)
file='src/WpBlueBubbles/MainPage.xaml.cs'
a,b=old(file),current(file)
methods = ['ResetMessageItems', 'RefreshMessagesAsync', 'ScrollToNewestMessageAsync', 'SendCurrentMessageAsync',
    'SendComposedMessageAsync', 'Attach_Click', 'ShowMessageActions', 'Media_Holding', 'Media_RightTapped',
    'ShowMediaContextMenu', 'ShowDarkActionFlyout', 'GetMediaFileName', 'ForwardMessageAsync', 'GetTileImageUriAsync',
    'PrepareSharedContent', 'BuildSharedPreview', 'StageSharedContentInComposer', 'ClearAttachment_Click',
    'CompleteSharedContent', 'FailSharedContent', 'ClearSharedContent', 'DeleteTemporarySharedFilesAsync',
    'PrepareVideoAsync', 'DescribeSharedFiles', 'AttachmentMedia_Failed', 'Media_Opened', 'Image_Tapped',
    'CloseImageViewer_Tapped', 'CloseImageViewer']
def method(source,name):
    start = re.search(r'^        (?:private|public|internal).*?\b' + re.escape(name) + r'\(',source,re.M)
    assert start, name
    end = re.search(r'^        (?:private|public|internal)\s',source[start.end():],re.M)
    return source[start.start():start.end()+end.start() if end else len(source)].rstrip()
for name in methods:
    before = normalize_branding(file, method(a,name))
    after = normalize_branding(file, method(b,name))
    assert before == after, name+' changed'
print('UNCHANGED',len(methods),'media/message methods')
file='src/WpBlueBubbles/MainPage.xaml'
for section in ['Page.Resources']:
    def extract(source): return source.split('<'+section+'>',1)[1].split('</'+section+'>',1)[0]
    assert extract(old(file))==extract(current(file)), section+' changed'
print('UNCHANGED XAML resources and message templates')
