# Golden values

`generate-golden.mjs` pulls `mb()`, `spd()`, `eta()`, `STATE_CN`, `SEED` and the
`SORT` map **out of the design handoff's own source** (`mini-ios2.jsx` and
`mini-ios2-app.jsx`) with regexes, evaluates them, and prints the results as
JSON. `NetTrans.Core.Tests` asserts against that JSON.

The point is that the test expectations are derived from the design rather than
from a hand transcription of it — if `FormatHelpers` disagrees with the
prototype about, say, how 62.5 MB rounds, the test fails instead of both sides
being wrong in the same way.

A handful of values cannot be extracted because they only exist inside JSX
markup (the row's sub line and trailing readout) or not at all in the prototype
(the easing curve solved by bisection, the mid-point rounding cases). Those are
written out in the generator next to the source they mirror.

## Regenerating

Needs Node and the handoff bundle unzipped somewhere:

```sh
unzip FlashgetMini.zip -d /tmp/handoff
node tools/golden/generate-golden.mjs /tmp/handoff/design_handoff_flashget_mini_v2 \
  > NetTrans.Core.Tests/Golden/golden.json
```
