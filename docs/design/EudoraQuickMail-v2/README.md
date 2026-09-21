# Eudora QuickMail icon concept v2

This is a non-destructive icon proposal derived from the blue paper-airplane/envelope mark used in the GitHub presentation image.

## Contents

- `EudoraQuickMail.ico`: multi-resolution Windows application icon.
- `EudoraQuickMailReceived.ico`: multi-resolution notification variant.
- `png/`: transparent PNG exports at 16, 24, 32, 48, 64, 128, 256, and 1024 pixels.
- `masters/`: unmodified generated source images.
- `preview-contact-sheet.png`: light/dark and small-size visual check.

The 16, 24, 32, and 48 px frames use edge-to-edge optical sizing so the wide
paper-plane mark has the same perceived taskbar weight as neighbouring icons.
Larger frames progressively restore the original presentation-image padding.

The production icons under `QuickMail/Assets/App/` are synchronized from this set.
