# Scanner benchmark

Measures how well the page detection of the document scanner (`src/MatPaper/wwwroot/js/scanner-core.js`, used by
`scanner.js`) finds a sheet of paper - and tunes it. It makes synthetic "photos of a sheet on a table" with a known
answer (the four corners), runs the detector over them in a browser and counts hits.

Nothing here is part of the app or the Docker image. It needs Node and a Chromium based browser (Edge by default,
`SCANNER_BENCH_BROWSER=chrome` for Chrome):

```bash
cd tools/scanner-bench
npm install
```

## Scenes

```bash
node gen-scenes.js all 1 40               # 40 scenes per category into scenes/ (development set)
node gen-scenes.js all 1001 40            # another 40 with other seeds: the hold-out set, never used for tuning
node gen-scenes.js sheet lightdesk 1 12 sheet.png   # look at a category (green = the true outline)
```

`scene-lib.js` draws them: desks (wood, white, dark, marble, granite, gingham cloth, carpet, keyboard, photographs),
sheets with letters, invoices, forms, notes, receipts, a dark letterhead or a printed frame, perspective, rotation,
curl, shadows of a hand, glare, fingers, pens, mugs, second sheets, blur, noise, JPEG, exposure and colour casts.
Categories: `easy lightdesk pattern shadow occluded clutter small cut receipt lowlight blurry curl tinted phoneport
highres nosheet` (the last has no sheet at all - any answer there is a false positive). A scene is a function of
(category, seed), so it can always be made again. Photographs as backgrounds are taken from the folder in
`SCANNER_BENCH_PHOTOS` (JPEGs over 200 KB), or from the Windows wallpapers if there are any; without them those scenes
get a plain desk.

## Measuring

```bash
node detect-bench.js                                 # the detector of this checkout, all categories, sensitivity "Medium"
node detect-bench.js --seeds 1001-1040 --level 1     # on the hold-out set
node detect-bench.js --det repo,old.js               # side by side with an older version (git show <commit>:src/MatPaper/wwwroot/js/scanner-core.js > old.js)
node detect-bench.js --cats cut,clutter --fails fails   # sheets of the scenes that went wrong (green truth, red/blue the detectors)
```

Per category: *good* = the outline covers the true sheet with IoU >= 0.93 (a crop one would not touch up), *ok* = IoU >= 0.85,
*wrong* = something was found but not the sheet, *miss* = nothing found, *iou* = mean overlap, and the time of one analysis.
For `nosheet`: how many of the scenes got an outline anyway.

Reading a failure: `node viz.js repo out.png easy/13 clutter/6` draws the lines the detector found and its best candidates,
`node why.js repo clutter/6` prints what the chosen candidate looked like against the right one.

## Tuning the judgement

Candidates (four lines that could bound a sheet) are rated by a linear model on their features (edge evidence of the sides,
shape, size, whiteness, calmness, lattice-likeness, border sides ...). Its weights are the `MODEL` block in
`scanner-core.js`; `LEVELS` there are the thresholds of the three sensitivities. To fit them again:

```bash
node detect-bench.js --cands d1.json                                    # the features of all candidates of all scenes
node detect-bench.js --cands d2.json --aug gain=0.55,contrast=0.85      # the same scenes dimmer (exposure / colour cast variants:
node detect-bench.js --cands d3.json --aug gain=0.75,tint=1.06:1:0.88   #   the scorer must not depend on how bright the photo is)
node learn2.js d1.json+d2.json+d3.json --drop off --emit model.json     # '+' keeps sets apart, ',' merges the candidates of one set
node embed-model.js model.json                                          # writes it into scanner-core.js
node detect-bench.js --seeds 1001-1040                                  # does it hold on scenes it has not seen?
```

`learn2.js` trains a softmax over the candidates of every scene (plus "no sheet") so that the candidate with the best IoU wins;
`--test dump.json` evaluates on another dump, `--cv` leaves one category out at a time. The feature list in `learn2.js`
(`ALL`) and in `rate()` of `scanner-core.js` must stay the same. A round of "hard negative mining" helps: dump again with the new
model embedded (the dump keeps the best candidates by the *current* score), train on both dumps together.

The numbers are about what the generator can draw, not about the world: a good value here is a necessary, not a sufficient
sign. A photograph that fails in practice is best turned into a new kind of scene (a new background, a new sheet style, a new
disturbance in `scene-lib.js`) so that it is measured from then on.

## The scanner itself

`scan-harness.html` loads the real `scanner-core.js` / `scanner.js` with the camera replaced by a canvas that a test draws on:

```bash
node scan-seq-test.js            # a page approaching, a shaking hand, panning, a hand in front, changing exposure, blurred frames:
                                 #   how often the outline is right, flicker, when the automatic release happens, the crop that results
node scan-live-test.js           # a still scene as the camera; compares the straightened page with the flat paper it was made from
node scan-native-test.js         # the "camera app" way: photos handed in, notes for "no page found" / "cut off"
```
