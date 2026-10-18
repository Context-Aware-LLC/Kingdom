// The iOS half of Kingdom.Session.SystemShareSheet (#424): one line of text into the system
// share sheet. On iPad the sheet is a popover and must be anchored, or presenting it crashes;
// it is anchored to the middle of the game view.

#import <UIKit/UIKit.h>

extern UIViewController* UnityGetGLViewController();

extern "C" void KobiShareText(const char* text)
{
    if (text == NULL) return;
    NSString* line = [NSString stringWithUTF8String:text];
    UIViewController* root = UnityGetGLViewController();
    if (line == nil || root == nil) return;

    UIActivityViewController* sheet =
        [[UIActivityViewController alloc] initWithActivityItems:@[line] applicationActivities:nil];
    UIPopoverPresentationController* popover = sheet.popoverPresentationController;
    if (popover != nil)
    {
        popover.sourceView = root.view;
        popover.sourceRect = CGRectMake(CGRectGetMidX(root.view.bounds), CGRectGetMidY(root.view.bounds), 0, 0);
        popover.permittedArrowDirections = 0;
    }
    [root presentViewController:sheet animated:YES completion:nil];
}
