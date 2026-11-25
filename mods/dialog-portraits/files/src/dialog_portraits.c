// Dialog portraits – a PokePat mod.
// Shows a 64x64 picture of the speaker above the message box:
//   setvar VAR_0x8004, PORTRAIT_X
//   special ShowPortrait
//   msgbox …
// The picture disappears when the message box closes (or with "special HidePortrait").
// Portraits are added in PokePat (Mods → Dialog portraits → Add portrait).
#include "global.h"
#include "sprite.h"
#include "event_data.h"
#include "dialog_portraits.h"
#include "constants/dialog_portraits.h"

#define PORTRAIT_TAG       0x5D50
#define PORTRAIT_X         (8 + 32)   // left edge of the message box + half the picture
#define PORTRAIT_Y         (112 - 32 - 4)

struct DialogPortrait
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

// The sprite id + 1 (0 = no portrait shown; EWRAM variables may only start as 0).
static EWRAM_DATA u8 sPortraitSpriteSlot = 0;

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

void ShowPortrait(void)
{
    u16 portrait = gSpecialVar_0x8004;
    struct SpriteSheet sheet;
    struct SpritePalette palette;

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
    sPortraitSpriteSlot = CreateSprite(&sPortraitTemplate, PORTRAIT_X, PORTRAIT_Y, 0) + 1;
}
