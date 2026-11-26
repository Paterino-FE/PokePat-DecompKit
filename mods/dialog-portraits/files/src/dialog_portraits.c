// Dialog portraits – a PokePat mod.
// Shows a 64x64 picture of the speaker above the message box:
//   setvar VAR_0x8004, PORTRAIT_X
//   special ShowPortrait                    (left)
// or, on the right and/or mirrored:
//   setvar VAR_0x8004, PORTRAIT_X
//   setvar VAR_0x8005, PORTRAIT_POS_RIGHT   (PORTRAIT_POS_LEFT, PORTRAIT_POS_RIGHT, + PORTRAIT_POS_MIRRORED)
//   special ShowPortraitAt
//   msgbox …
// The picture disappears when the message box closes (or with "special HidePortrait").
// A bigger picture (up to 128x96, e.g. a map or a letter) in the middle of the screen:
//   setvar VAR_0x8004, PICTURE_X
//   special ShowPicture
// It disappears when the message box closes (or with "special HidePicture").
// Portraits and pictures are added in PokePat (Mods → Dialog portraits).
#include "global.h"
#include "sprite.h"
#include "event_data.h"
#include "dialog_portraits.h"
#include "constants/dialog_portraits.h"

#define PORTRAIT_TAG       0x5D50
#define PORTRAIT_X         (8 + 32)   // left edge of the message box + half the picture
#define PORTRAIT_X_RIGHT   (DISPLAY_WIDTH - 8 - 32)
#define PORTRAIT_Y         (112 - 32 - 4)

// Pictures: 128x96, six 64x32 pieces (2 columns, 3 rows), stored one below the other (a 64x192 sheet).
#define PICTURE_TAG        0x5D51
#define PICTURE_PIECES     6
#define PICTURE_LEFT       ((DISPLAY_WIDTH - 128) / 2)
#define PICTURE_TOP        8

struct DialogPortrait
{
    const u32 *gfx;
    const u16 *pal;
};

struct DialogPicture
{
    const u32 *gfx;
    const u16 *pal;
};

#include "data/dialog_portraits.h"

static const struct OamData sPortraitOam =
{
    .affineMode = ST_OAM_AFFINE_OFF,
    .objMode = ST_OAM_OBJ_NORMAL,
    .bpp = ST_OAM_4BPP,
    .shape = SPRITE_SHAPE(64x64),
    .size = SPRITE_SIZE(64x64),
    .priority = 0,
};

static const struct SpriteTemplate sPortraitTemplate =
{
    .tileTag = PORTRAIT_TAG,
    .paletteTag = PORTRAIT_TAG,
    .oam = &sPortraitOam,
    .anims = gDummySpriteAnimTable,
    .images = NULL,
    .affineAnims = gDummySpriteAffineAnimTable,
    .callback = SpriteCallbackDummy,
};

static const struct OamData sPictureOam =
{
    .affineMode = ST_OAM_AFFINE_OFF,
    .objMode = ST_OAM_OBJ_NORMAL,
    .bpp = ST_OAM_4BPP,
    .shape = SPRITE_SHAPE(64x32),
    .size = SPRITE_SIZE(64x32),
    .priority = 0,
};

// Each piece shows its own 32 tiles of the sheet.
static const union AnimCmd sPictureAnim0[] = { ANIMCMD_FRAME(0, 0), ANIMCMD_END };
static const union AnimCmd sPictureAnim1[] = { ANIMCMD_FRAME(32, 0), ANIMCMD_END };
static const union AnimCmd sPictureAnim2[] = { ANIMCMD_FRAME(64, 0), ANIMCMD_END };
static const union AnimCmd sPictureAnim3[] = { ANIMCMD_FRAME(96, 0), ANIMCMD_END };
static const union AnimCmd sPictureAnim4[] = { ANIMCMD_FRAME(128, 0), ANIMCMD_END };
static const union AnimCmd sPictureAnim5[] = { ANIMCMD_FRAME(160, 0), ANIMCMD_END };
static const union AnimCmd *const sPictureAnims[] = { sPictureAnim0, sPictureAnim1, sPictureAnim2, sPictureAnim3, sPictureAnim4, sPictureAnim5 };

static const struct SpriteTemplate sPictureTemplate =
{
    .tileTag = PICTURE_TAG,
    .paletteTag = PICTURE_TAG,
    .oam = &sPictureOam,
    .anims = sPictureAnims,
    .images = NULL,
    .affineAnims = gDummySpriteAffineAnimTable,
    .callback = SpriteCallbackDummy,
};

// The sprite id + 1 (0 = no portrait shown; EWRAM variables may only start as 0).
static EWRAM_DATA u8 sPortraitSpriteSlot = 0;
static EWRAM_DATA u8 sPictureSpriteSlots[PICTURE_PIECES] = {0};

void HidePortrait(void)
{
    u8 id = sPortraitSpriteSlot - 1;
    if (sPortraitSpriteSlot == 0)
        return;
    sPortraitSpriteSlot = 0;
    // After a map change the sprite memory was reset: only remove the sprite if it is still ours.
    if (id < MAX_SPRITES && gSprites[id].inUse && gSprites[id].template == &sPortraitTemplate)
        DestroySprite(&gSprites[id]);
    FreeSpriteTilesByTag(PORTRAIT_TAG);
    FreeSpritePaletteByTag(PORTRAIT_TAG);
}

static void ShowPortraitWith(u16 portrait, u16 position)
{
    struct SpriteSheet sheet;
    struct SpritePalette palette;
    u8 id;

    HidePortrait();
    if (portrait == PORTRAIT_NONE || portrait >= ARRAY_COUNT(sDialogPortraits) || sDialogPortraits[portrait].gfx == NULL)
        return;
    sheet.data = sDialogPortraits[portrait].gfx;
    sheet.size = 64 * 64 / 2;
    sheet.tag = PORTRAIT_TAG;
    palette.data = sDialogPortraits[portrait].pal;
    palette.tag = PORTRAIT_TAG;
    LoadSpriteSheet(&sheet);
    LoadSpritePalette(&palette);
    id = CreateSprite(&sPortraitTemplate, (position & PORTRAIT_POS_RIGHT) ? PORTRAIT_X_RIGHT : PORTRAIT_X, PORTRAIT_Y, 0);
    if (id == MAX_SPRITES)
        return;
    // hFlip survives the sprite's animation updates, matrixNum shows it right away.
    gSprites[id].hFlip = (position & PORTRAIT_POS_MIRRORED) ? TRUE : FALSE;
    gSprites[id].oam.matrixNum = (position & PORTRAIT_POS_MIRRORED) ? ST_OAM_HFLIP : 0;
    sPortraitSpriteSlot = id + 1;
}

void ShowPortrait(void)
{
    ShowPortraitWith(gSpecialVar_0x8004, PORTRAIT_POS_LEFT);
}

void ShowPortraitAt(void)
{
    ShowPortraitWith(gSpecialVar_0x8004, gSpecialVar_0x8005);
}

void HidePicture(void)
{
    u32 i;
    bool32 shown = FALSE;

    for (i = 0; i < PICTURE_PIECES; i++)
    {
        u8 id = sPictureSpriteSlots[i] - 1;
        if (sPictureSpriteSlots[i] == 0)
            continue;
        shown = TRUE;
        sPictureSpriteSlots[i] = 0;
        if (id < MAX_SPRITES && gSprites[id].inUse && gSprites[id].template == &sPictureTemplate)
            DestroySprite(&gSprites[id]);
    }
    if (shown)
    {
        FreeSpriteTilesByTag(PICTURE_TAG);
        FreeSpritePaletteByTag(PICTURE_TAG);
    }
}

void ShowPicture(void)
{
    u16 picture = gSpecialVar_0x8004;
    struct SpriteSheet sheet;
    struct SpritePalette palette;
    u32 i;

    HidePicture();
    if (picture == PICTURE_NONE || picture >= ARRAY_COUNT(sDialogPictures) || sDialogPictures[picture].gfx == NULL)
        return;
    sheet.data = sDialogPictures[picture].gfx;
    sheet.size = 128 * 96 / 2;
    sheet.tag = PICTURE_TAG;
    palette.data = sDialogPictures[picture].pal;
    palette.tag = PICTURE_TAG;
    // Not enough free sprite memory (many people and weather on the map): show nothing instead of garbage.
    if (LoadSpriteSheet(&sheet) == 0)
        return;
    if (LoadSpritePalette(&palette) == 0xFF)
    {
        FreeSpriteTilesByTag(PICTURE_TAG);
        return;
    }
    for (i = 0; i < PICTURE_PIECES; i++)
    {
        u8 id = CreateSprite(&sPictureTemplate, PICTURE_LEFT + 32 + (i % 2) * 64, PICTURE_TOP + 16 + (i / 2) * 32, 0);
        if (id == MAX_SPRITES)
            break;
        StartSpriteAnim(&gSprites[id], i);
        sPictureSpriteSlots[i] = id + 1;
    }
}
