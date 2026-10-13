#pragma once
#include "output.h"

namespace lp {

int runHooks(Output& out);     // installs WH_MOUSE_LL / WH_KEYBOARD_LL and pumps messages

}  // namespace lp
