#include "level_caps.h"

#include "constants/pokemon.h"
#include "generated/badges.h"

// PokePat mod platinum-level-caps. The numbers can be changed in PokePat (Mods → Level caps by badges → Settings).
typedef struct LevelCapEntry {
    enum Badge badge;
    u8 levelCap;
} LevelCapEntry;

// The cap while the badge is still missing (= the level of that Gym Leader's strongest Pokémon).
static const LevelCapEntry sLevelCaps[] = {
    { BADGE_ID_COAL, 14 },   // Roark
    { BADGE_ID_FOREST, 22 }, // Gardenia
    { BADGE_ID_RELIC, 26 },  // Fantina
    { BADGE_ID_COBBLE, 32 }, // Maylene
    { BADGE_ID_FEN, 37 },    // Crasher Wake
    { BADGE_ID_MINE, 41 },   // Byron
    { BADGE_ID_ICICLE, 44 }, // Candice
    { BADGE_ID_BEACON, 50 }, // Volkner
};

// All eight badges, before the Champion is beaten.
#define LEVEL_CAP_LEAGUE 62

u8 LevelCaps_Get(u32 badgeMask, BOOL gameCompleted)
{
    for (int i = 0; i < (int)(sizeof(sLevelCaps) / sizeof(sLevelCaps[0])); i++) {
        if (!(badgeMask & (1 << sLevelCaps[i].badge))) {
            return sLevelCaps[i].levelCap;
        }
    }

    if (gameCompleted == FALSE) {
        return LEVEL_CAP_LEAGUE;
    }

    return MAX_POKEMON_LEVEL;
}
