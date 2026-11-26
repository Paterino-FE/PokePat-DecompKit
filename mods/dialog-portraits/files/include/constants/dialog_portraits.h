#ifndef GUARD_CONSTANTS_DIALOG_PORTRAITS_H
#define GUARD_CONSTANTS_DIALOG_PORTRAITS_H

// Dialog portraits – a PokePat mod. PokePat adds a constant for every portrait.
#define PORTRAIT_NONE 0

// Where ShowPortraitAt shows it (VAR_0x8005); add PORTRAIT_POS_MIRRORED to mirror the picture.
#define PORTRAIT_POS_LEFT     0
#define PORTRAIT_POS_RIGHT    1
#define PORTRAIT_POS_MIRRORED 2
#define PORTRAIT_POS_LEFT_MIRRORED  (PORTRAIT_POS_LEFT | PORTRAIT_POS_MIRRORED)
#define PORTRAIT_POS_RIGHT_MIRRORED (PORTRAIT_POS_RIGHT | PORTRAIT_POS_MIRRORED)

// Pictures (special ShowPicture): PokePat adds a constant for every picture.
#define PICTURE_NONE 0

#endif // GUARD_CONSTANTS_DIALOG_PORTRAITS_H
