#ifndef POKEPLATINUM_FIELD_MUGSHOT_H
#define POKEPLATINUM_FIELD_MUGSHOT_H

#include "field/field_system_decl.h"

#include "field_script_context.h"

// PokePat mod platinum-mugshots
void FieldMugshot_Set(u16 mugshot);
void FieldMugshot_SetSide(u16 side);
BOOL FieldMugshot_IsSet(void);
void FieldMugshot_Draw(FieldSystem *fieldSystem);
void FieldMugshot_Clear(void);
void FieldPicture_Show(FieldSystem *fieldSystem, u16 picture);
void FieldPicture_Hide(void);
BOOL ScrCmd_ShowMugshot(ScriptContext *ctx);
BOOL ScrCmd_ClearMugshot(ScriptContext *ctx);
BOOL ScrCmd_ShowPicture(ScriptContext *ctx);
BOOL ScrCmd_HidePicture(ScriptContext *ctx);
BOOL ScrCmd_ShowFullscreenPicture(ScriptContext *ctx);

#endif // POKEPLATINUM_FIELD_MUGSHOT_H
