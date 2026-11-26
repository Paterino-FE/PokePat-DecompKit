#include "field_mugshot.h"

#include <nitro.h>

#include "constants/field_base_tiles.h"
#include "constants/graphics.h"
#include "constants/heap.h"
#include "constants/mugshots.h"
#include "constants/narc.h"

#include "field/field_system.h"

#include "bg_window.h"
#include "field_script_context.h"
#include "field_system.h"
#include "field_task.h"
#include "graphics.h"
#include "gx_layers.h"
#include "heap.h"
#include "inlines.h"
#include "overlay_manager.h"
#include "screen_fade.h"
#include "system.h"

#include "data/field_pictures.h"

// PokePat mod platinum-mugshots.
// - Mugshots: a 64x64 picture above the left or right end of the message box while a script talks (left = mirrored, so the
//   person looks towards the text). It goes away with the message box.
// - Pictures: a bigger picture (e.g. a treasure map), centred above the message box, until HidePicture or CloseMessage.
// The graphics are stored after the 9 map popups in arc/area_win_gra.narc: first all mugshots, then all pictures
// (graphics + palette each). They use the tiles of the map-name drop-down (only used while a map name drops in) and the
// palettes 14 and 15, which the field does not use – the text (13), message box (10, 12) and frame (11) palettes stay.
// - Full-screen pictures (ShowFullscreenPicture): 256x192 with 255 colours on a screen of their own (like the Town Map):
//   the field fades out, the picture fades in, A or B closes it and the field comes back. They are stored after the
//   pictures (8-bit graphics + 256-colour palette each).
#define MUGSHOT_FIRST_MEMBER 18
#define PICTURE_FIRST_MEMBER (MUGSHOT_FIRST_MEMBER + 2 * (MUGSHOT_COUNT - 1))
#define FULLSCREEN_FIRST_MEMBER (PICTURE_FIRST_MEMBER + 2 * (PICTURE_COUNT - 1))
#define MUGSHOT_PALETTE      14
#define PICTURE_PALETTE      15
#define MUGSHOT_TILE_Y       10
#define MUGSHOT_TILE_X_RIGHT 23
#define MUGSHOT_TILE_X_LEFT  1
#define MUGSHOT_TILES        8
#define MUGSHOT_BASE_TILE    (BASE_TILE_MAP_TRANSITION_DROPDOWN)
#define PICTURE_BASE_TILE    (MUGSHOT_BASE_TILE + MUGSHOT_TILES * MUGSHOT_TILES)
#define PICTURE_MAX_TILES    (MAP_TRANSITION_DROPDOWN_TILE_COUNT - MUGSHOT_TILES * MUGSHOT_TILES)
#define SCREEN_TILES_W       32
#define MESSAGE_BOX_TOP      19

static u16 sCurrentMugshot = MUGSHOT_NONE;
static u16 sMugshotSide = MUGSHOT_POS_RIGHT;
static Window sMugshotWindow;
static BOOL sMugshotWindowOpen = FALSE;
static Window sPictureWindow;
static BOOL sPictureWindowOpen = FALSE;

void FieldMugshot_Set(u16 mugshot)
{
    sCurrentMugshot = mugshot < MUGSHOT_COUNT ? mugshot : MUGSHOT_NONE;
}

void FieldMugshot_SetSide(u16 side)
{
    sMugshotSide = side == MUGSHOT_POS_LEFT ? MUGSHOT_POS_LEFT : MUGSHOT_POS_RIGHT;
}

BOOL FieldMugshot_IsSet(void)
{
    return sCurrentMugshot != MUGSHOT_NONE;
}

// One 8x8 4bpp tile mirrored left-right: the bytes of a row in reverse order, the two pixels of each byte swapped.
static void MirrorTile(const u8 *src, u8 *dst)
{
    for (int row = 0; row < 8; row++) {
        for (int i = 0; i < 4; i++) {
            u8 b = src[row * 4 + i];
            dst[row * 4 + (3 - i)] = (u8)((b << 4) | (b >> 4));
        }
    }
}

// Copies tilesW x tilesH tiles (row by row, as nitrogfx stores a picture) into the window, optionally mirrored.
static void BlitTiles(Window *window, const u8 *data, int tilesW, int tilesH, BOOL mirror)
{
    u8 mirrored[TILE_SIZE_4BPP];

    for (int y = 0; y < tilesH; y++) {
        for (int x = 0; x < tilesW; x++) {
            const u8 *tile = data + (y * tilesW + x) * TILE_SIZE_4BPP;
            int destX = x;

            if (mirror) {
                MirrorTile(tile, mirrored);
                tile = mirrored;
                destX = tilesW - 1 - x;
            }

            Window_BlitBitmapRect(window, (void *)tile, 0, 0, 8, 8, destX * 8, y * 8, 8, 8);
        }
    }
}

void FieldMugshot_Draw(FieldSystem *fieldSystem)
{
    if (sCurrentMugshot == MUGSHOT_NONE || sMugshotWindowOpen == TRUE) {
        return;
    }

    NNSG2dCharacterData *charData;
    NNSG2dPaletteData *paletteData;
    u32 member = MUGSHOT_FIRST_MEMBER + (sCurrentMugshot - 1) * 2;
    BOOL left = sMugshotSide == MUGSHOT_POS_LEFT;

    Window_Init(&sMugshotWindow);
    Window_Add(fieldSystem->bgConfig, &sMugshotWindow, BG_LAYER_MAIN_3, left ? MUGSHOT_TILE_X_LEFT : MUGSHOT_TILE_X_RIGHT, MUGSHOT_TILE_Y, MUGSHOT_TILES, MUGSHOT_TILES, MUGSHOT_PALETTE, MUGSHOT_BASE_TILE);
    Window_FillTilemap(&sMugshotWindow, 0);

    void *tiles = Graphics_GetCharData(NARC_INDEX_ARC__AREA_WIN_GRA, member, FALSE, &charData, HEAP_ID_FIELD1);
    void *palette = Graphics_GetPlttData(NARC_INDEX_ARC__AREA_WIN_GRA, member + 1, &paletteData, HEAP_ID_FIELD1);

    GX_LoadBGPltt(paletteData->pRawData, MUGSHOT_PALETTE * 32, 32);
    BlitTiles(&sMugshotWindow, (const u8 *)charData->pRawData, MUGSHOT_TILES, MUGSHOT_TILES, left);

    Heap_Free(tiles);
    Heap_Free(palette);
    Window_CopyToVRAM(&sMugshotWindow);
    sMugshotWindowOpen = TRUE;
}

void FieldMugshot_Clear(void)
{
    if (sMugshotWindowOpen == FALSE) {
        return;
    }

    Window_ClearAndCopyToVRAM(&sMugshotWindow);
    Window_Remove(&sMugshotWindow);
    sMugshotWindowOpen = FALSE;
}

void FieldPicture_Show(FieldSystem *fieldSystem, u16 picture)
{
    FieldPicture_Hide();

    if (picture == PICTURE_NONE || picture >= PICTURE_COUNT) {
        return;
    }

    int tilesW = sFieldPictureSize[picture][0];
    int tilesH = sFieldPictureSize[picture][1];

    if (tilesW == 0 || tilesH == 0 || tilesW * tilesH > PICTURE_MAX_TILES || tilesW > SCREEN_TILES_W || tilesH > MESSAGE_BOX_TOP) {
        return;
    }

    NNSG2dCharacterData *charData;
    NNSG2dPaletteData *paletteData;
    u32 member = PICTURE_FIRST_MEMBER + (picture - 1) * 2;

    // Centred in the part of the screen above the message box.
    Window_Init(&sPictureWindow);
    Window_Add(fieldSystem->bgConfig, &sPictureWindow, BG_LAYER_MAIN_3, (SCREEN_TILES_W - tilesW) / 2, (MESSAGE_BOX_TOP - tilesH) / 2, tilesW, tilesH, PICTURE_PALETTE, PICTURE_BASE_TILE);
    Window_FillTilemap(&sPictureWindow, 0);

    void *tiles = Graphics_GetCharData(NARC_INDEX_ARC__AREA_WIN_GRA, member, FALSE, &charData, HEAP_ID_FIELD1);
    void *palette = Graphics_GetPlttData(NARC_INDEX_ARC__AREA_WIN_GRA, member + 1, &paletteData, HEAP_ID_FIELD1);

    GX_LoadBGPltt(paletteData->pRawData, PICTURE_PALETTE * 32, 32);
    BlitTiles(&sPictureWindow, (const u8 *)charData->pRawData, tilesW, tilesH, FALSE);

    Heap_Free(tiles);
    Heap_Free(palette);
    Window_CopyToVRAM(&sPictureWindow);
    sPictureWindowOpen = TRUE;
}

void FieldPicture_Hide(void)
{
    if (sPictureWindowOpen == FALSE) {
        return;
    }

    Window_ClearAndCopyToVRAM(&sPictureWindow);
    Window_Remove(&sPictureWindow);
    sPictureWindowOpen = FALSE;
}

BOOL ScrCmd_ShowMugshot(ScriptContext *ctx)
{
    FieldMugshot_Set(ScriptContext_GetVar(ctx));
    FieldMugshot_SetSide(ScriptContext_GetVar(ctx));
    return FALSE;
}

BOOL ScrCmd_ClearMugshot(ScriptContext *ctx)
{
    FieldMugshot_Set(MUGSHOT_NONE);
    FieldMugshot_Clear();
    return FALSE;
}

BOOL ScrCmd_ShowPicture(ScriptContext *ctx)
{
    FieldPicture_Show(ctx->fieldSystem, ScriptContext_GetVar(ctx));
    return FALSE;
}

BOOL ScrCmd_HidePicture(ScriptContext *ctx)
{
    FieldPicture_Hide();
    return FALSE;
}

// ---- Full-screen pictures: a small application of its own, started from a script like the Town Map.

#define FULLSCREEN_HEAP_ID   HEAP_ID_TOWN_MAP   // never open at the same time as the Town Map
#define FULLSCREEN_HEAP_SIZE 0x20000
#define FULLSCREEN_TILES_W   32
#define FULLSCREEN_TILES_H   24

enum FullscreenPictureState {
    FULLSCREEN_STATE_INIT,
    FULLSCREEN_STATE_FADE_IN,
    FULLSCREEN_STATE_SHOW,
    FULLSCREEN_STATE_FADE_OUT,
};

typedef struct FullscreenPicture {
    BgConfig *bgConfig;
    u16 picture;
} FullscreenPicture;

static u16 sFullscreenPictureArg = FULLSCREEN_NONE;

static void FullscreenPicture_VBlank(void *data)
{
    FullscreenPicture *app = data;
    Bg_RunScheduledUpdates(app->bgConfig);
}

static void FullscreenPicture_SetUp(FullscreenPicture *app)
{
    GXBanks banks = {
        GX_VRAM_BG_256_AB,
        GX_VRAM_BGEXTPLTT_NONE,
        GX_VRAM_SUB_BG_NONE,
        GX_VRAM_SUB_BGEXTPLTT_NONE,
        GX_VRAM_OBJ_NONE,
        GX_VRAM_OBJEXTPLTT_NONE,
        GX_VRAM_SUB_OBJ_NONE,
        GX_VRAM_SUB_OBJEXTPLTT_NONE,
        GX_VRAM_TEX_NONE,
        GX_VRAM_TEXPLTT_NONE
    };
    GraphicsModes modes = {
        GX_DISPMODE_GRAPHICS,
        GX_BGMODE_0,
        GX_BGMODE_0,
        GX_BG0_AS_2D
    };
    BgTemplate layer = {
        .x = 0,
        .y = 0,
        .bufferSize = 0x800,
        .baseTile = 0,
        .screenSize = BG_SCREEN_SIZE_256x256,
        .colorMode = GX_BG_COLORMODE_256,
        .screenBase = GX_BG_SCRBASE_0x0000,
        .charBase = GX_BG_CHARBASE_0x10000,
        .bgExtPltt = GX_BG_EXTPLTT_01,
        .priority = 0,
        .areaOver = 0,
        .mosaic = FALSE,
    };
    u32 member = FULLSCREEN_FIRST_MEMBER + (app->picture - 1) * 2;
    u16 *tilemap;
    int i;

    GXLayers_SetBanks(&banks);
    app->bgConfig = BgConfig_New(FULLSCREEN_HEAP_ID);
    SetAllGraphicsModes(&modes);
    Bg_InitFromTemplate(app->bgConfig, BG_LAYER_MAIN_0, &layer, 0);
    Bg_ClearTilemap(app->bgConfig, BG_LAYER_MAIN_0);

    // The picture is 32x24 tiles, one after the other: tile i goes to place i of the screen.
    Graphics_LoadTilesToBgLayer(NARC_INDEX_ARC__AREA_WIN_GRA, member, app->bgConfig, BG_LAYER_MAIN_0, 0, 0, FALSE, FULLSCREEN_HEAP_ID);
    Graphics_LoadPalette(NARC_INDEX_ARC__AREA_WIN_GRA, member + 1, PAL_LOAD_MAIN_BG, 0, 0x200, FULLSCREEN_HEAP_ID);

    tilemap = Heap_Alloc(FULLSCREEN_HEAP_ID, 0x800);
    for (i = 0; i < 32 * 32; i++) {
        tilemap[i] = i < FULLSCREEN_TILES_W * FULLSCREEN_TILES_H ? i : 0;
    }
    Bg_LoadTilemapBuffer(app->bgConfig, BG_LAYER_MAIN_0, tilemap, 0x800);
    Bg_CopyTilemapBufferToVRAM(app->bgConfig, BG_LAYER_MAIN_0);
    Heap_Free(tilemap);

    // The lower screen stays black.
    {
        u16 black = 0;
        GXS_LoadBGPltt(&black, 0, sizeof(black));
    }
    Bg_ToggleLayer(BG_LAYER_MAIN_0, TRUE);
}

static void FullscreenPicture_TearDown(FullscreenPicture *app)
{
    Bg_ToggleLayer(BG_LAYER_MAIN_0, FALSE);
    Bg_FreeTilemapBuffer(app->bgConfig, BG_LAYER_MAIN_0);
    Heap_Free(app->bgConfig);
}

static BOOL FullscreenPicture_Init(ApplicationManager *appMan, int *state)
{
    FullscreenPicture *app;

    Heap_Create(HEAP_ID_APPLICATION, FULLSCREEN_HEAP_ID, FULLSCREEN_HEAP_SIZE);
    app = ApplicationManager_NewData(appMan, sizeof(FullscreenPicture), FULLSCREEN_HEAP_ID);
    app->bgConfig = NULL;
    app->picture = *(u16 *)ApplicationManager_Args(appMan);
    return TRUE;
}

static BOOL FullscreenPicture_Main(ApplicationManager *appMan, int *state)
{
    FullscreenPicture *app = ApplicationManager_Data(appMan);

    switch (*state) {
    case FULLSCREEN_STATE_INIT:
        SetScreenColorBrightness(DS_SCREEN_MAIN, COLOR_BLACK);
        SetScreenColorBrightness(DS_SCREEN_SUB, COLOR_BLACK);
        SetVBlankCallback(NULL, NULL);
        SetHBlankCallback(NULL, NULL);
        GXLayers_DisableEngineALayers();
        GXLayers_DisableEngineBLayers();
        GX_SetVisiblePlane(0);
        GXS_SetVisiblePlane(0);
        FullscreenPicture_SetUp(app);
        SetVBlankCallback(FullscreenPicture_VBlank, app);
        GXLayers_TurnBothDispOn();
        StartScreenFade(FADE_BOTH_SCREENS, FADE_TYPE_BRIGHTNESS_IN, FADE_TYPE_BRIGHTNESS_IN, COLOR_BLACK, 6, 1, FULLSCREEN_HEAP_ID);
        *state = FULLSCREEN_STATE_FADE_IN;
        break;
    case FULLSCREEN_STATE_FADE_IN:
        if (IsScreenFadeDone() == TRUE) {
            *state = FULLSCREEN_STATE_SHOW;
        }
        break;
    case FULLSCREEN_STATE_SHOW:
        if (JOY_NEW(PAD_BUTTON_A | PAD_BUTTON_B)) {
            StartScreenFade(FADE_BOTH_SCREENS, FADE_TYPE_BRIGHTNESS_OUT, FADE_TYPE_BRIGHTNESS_OUT, COLOR_BLACK, 6, 1, FULLSCREEN_HEAP_ID);
            *state = FULLSCREEN_STATE_FADE_OUT;
        }
        break;
    case FULLSCREEN_STATE_FADE_OUT:
        if (IsScreenFadeDone() == TRUE) {
            SetVBlankCallback(NULL, NULL);
            FullscreenPicture_TearDown(app);
            return TRUE;
        }
        break;
    }
    return FALSE;
}

static BOOL FullscreenPicture_Exit(ApplicationManager *appMan, int *state)
{
    ApplicationManager_FreeData(appMan);
    Heap_Destroy(FULLSCREEN_HEAP_ID);
    return TRUE;
}

static const ApplicationManagerTemplate sFullscreenPictureApp = {
    FullscreenPicture_Init,
    FullscreenPicture_Main,
    FullscreenPicture_Exit,
    FS_OVERLAY_ID_NONE
};

static BOOL ScriptContext_WaitForFullscreenPicture(ScriptContext *ctx)
{
    return FieldSystem_IsRunningApplication(ctx->fieldSystem) ? FALSE : TRUE;
}

// The script fades the field out before (the macro ShowFullscreenPicture does it) and calls ReturnToField after.
BOOL ScrCmd_ShowFullscreenPicture(ScriptContext *ctx)
{
    u16 picture = ScriptContext_GetVar(ctx);

    if (picture == FULLSCREEN_NONE || picture >= FULLSCREEN_COUNT) {
        return FALSE;
    }
    sFullscreenPictureArg = picture;
    FieldSystem_StartChildProcess(ctx->fieldSystem, &sFullscreenPictureApp, &sFullscreenPictureArg);
    ScriptContext_Pause(ctx, ScriptContext_WaitForFullscreenPicture);
    return TRUE;
}
