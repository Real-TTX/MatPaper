// Document scanner core: find the sheet in a photo, straighten it, clean it up.
// Pure image code on canvas pixels - no dependencies, runs in the browser (and is testable there).
//
//   MatPaperScan.detect(source, { sensitivity, hint, precise }) -> { quad:[tl,tr,br,bl] (0..1), score, margin, area, cut } | null
//   MatPaperScan.overlap(quadA, quadB)               -> 0..1 (how much two outlines cover each other)
//   MatPaperScan.warp(canvas, quad, maxSide)         -> canvas (the sheet, straightened)
//   MatPaperScan.filter(canvas, "color|enhanced|gray|bw") -> canvas
//   MatPaperScan.rotate(canvas, quarterTurns)        -> canvas
//
// Detection looks for the sides of the sheet, not for a bright blob: the long straight edges of the picture are found, every
// set of four that could bound a sheet is a candidate, and each candidate is judged by how much of each side is a real step
// between the sheet and what lies around it, by its shape, size and inside. See the comments in the detect section.
(function (root) {
    "use strict";

    // ------------------------------------------------------------------ helpers

    function makeCanvas(w, h) {
        var c = document.createElement("canvas");
        c.width = Math.max(1, Math.round(w));
        c.height = Math.max(1, Math.round(h));
        return c;
    }

    function toGray(imageData) {
        var d = imageData.data, n = imageData.width * imageData.height, g = new Float32Array(n);
        for (var i = 0, p = 0; i < n; i++, p += 4) {
            g[i] = 0.299 * d[p] + 0.587 * d[p + 1] + 0.114 * d[p + 2];
        }
        return g;
    }

    // Separable box blur with running sums; edges are clamped.
    function boxBlur(src, w, h, r) {
        if (r < 1) { return src; }
        var tmp = new Float32Array(src.length), out = new Float32Array(src.length);
        var x, y, sum, count, i;
        for (y = 0; y < h; y++) {
            sum = 0; count = 0;
            for (x = -r; x <= r; x++) { i = Math.min(w - 1, Math.max(0, x)); sum += src[y * w + i]; count++; }
            for (x = 0; x < w; x++) {
                tmp[y * w + x] = sum / count;
                var add = Math.min(w - 1, x + r + 1), rem = Math.max(0, x - r);
                sum += src[y * w + add] - src[y * w + rem];
            }
        }
        for (x = 0; x < w; x++) {
            sum = 0; count = 0;
            for (y = -r; y <= r; y++) { i = Math.min(h - 1, Math.max(0, y)); sum += tmp[i * w + x]; count++; }
            for (y = 0; y < h; y++) {
                out[y * w + x] = sum / count;
                var add2 = Math.min(h - 1, y + r + 1), rem2 = Math.max(0, y - r);
                sum += tmp[add2 * w + x] - tmp[rem2 * w + x];
            }
        }
        return out;
    }

    function cross(o, a, b) { return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]); }

    function convexHull(points) {
        points.sort(function (a, b) { return a[0] - b[0] || a[1] - b[1]; });
        var lower = [], i;
        for (i = 0; i < points.length; i++) {
            while (lower.length >= 2 && cross(lower[lower.length - 2], lower[lower.length - 1], points[i]) <= 0) { lower.pop(); }
            lower.push(points[i]);
        }
        var upper = [];
        for (i = points.length - 1; i >= 0; i--) {
            while (upper.length >= 2 && cross(upper[upper.length - 2], upper[upper.length - 1], points[i]) <= 0) { upper.pop(); }
            upper.push(points[i]);
        }
        upper.pop(); lower.pop();
        return lower.concat(upper);
    }

    function polygonArea(p) {
        var a = 0;
        for (var i = 0; i < p.length; i++) { var j = (i + 1) % p.length; a += p[i][0] * p[j][1] - p[j][0] * p[i][1]; }
        return Math.abs(a) / 2;
    }

    // Drop the vertex that contributes the least area until four are left.
    function reduceToQuad(hull) {
        var p = hull.slice();
        while (p.length > 4) {
            var minArea = Infinity, minIndex = 0;
            for (var i = 0; i < p.length; i++) {
                var a = p[(i + p.length - 1) % p.length], b = p[i], c = p[(i + 1) % p.length];
                var area = Math.abs(cross(a, b, c)) / 2;
                if (area < minArea) { minArea = area; minIndex = i; }
            }
            p.splice(minIndex, 1);
        }
        return p;
    }

    // Order as top-left, top-right, bottom-right, bottom-left.
    function orderQuad(q) {
        var cx = 0, cy = 0, i;
        for (i = 0; i < 4; i++) { cx += q[i][0] / 4; cy += q[i][1] / 4; }
        var sorted = q.slice().sort(function (a, b) {
            return Math.atan2(a[1] - cy, a[0] - cx) - Math.atan2(b[1] - cy, b[0] - cx);
        });
        // sorted is clockwise starting from the left (screen y points down); rotate so index 0 is top-left
        var start = 0, bestSum = Infinity;
        for (i = 0; i < 4; i++) { var s = sorted[i][0] + sorted[i][1]; if (s < bestSum) { bestSum = s; start = i; } }
        return [sorted[start], sorted[(start + 1) % 4], sorted[(start + 2) % 4], sorted[(start + 3) % 4]];
    }

    function sideLength(a, b) { return Math.hypot(a[0] - b[0], a[1] - b[1]); }

    // How close the corners are to right angles (perspective bends them, so this is tolerant).
    function rectangularity(q) {
        var sum = 0;
        for (var i = 0; i < 4; i++) {
            var p = q[(i + 3) % 4], c = q[i], n = q[(i + 1) % 4];
            var v1 = [p[0] - c[0], p[1] - c[1]], v2 = [n[0] - c[0], n[1] - c[1]];
            var cos = (v1[0] * v2[0] + v1[1] * v2[1]) / Math.max(1e-6, Math.hypot(v1[0], v1[1]) * Math.hypot(v2[0], v2[1]));
            var angle = Math.acos(Math.max(-1, Math.min(1, cos))) * 180 / Math.PI;
            sum += Math.abs(angle - 90);
        }
        return Math.max(0, 1 - (sum / 4) / 38);
    }

    // ------------------------------------------------------------------ detect
    //
    // How a sheet is found - all on a small copy of the picture (about 400 px on the long side):
    //  1. edges: the gradient of a mix of grey and "whiteness" (the darkest colour channel: high only for bright, neutral things),
    //     thinned to one pixel;
    //  2. lines: a Hough transform that votes with the direction of the gradient (so it also knows which side is the brighter one)
    //     finds the long straight edges, each one is then fitted exactly to the edge pixels that belong to it. Every side is found
    //     on its own, so a finger over a corner, a dark letterhead or a shadow does not break the others;
    //  3. candidates: every set of four lines whose inward normals turn clockwise by about 90 degrees from one to the next is a
    //     possible outline - also with a side on the border of the picture (a sheet that goes on beyond it);
    //  4. rating: a candidate gets numbers - how much of each side is a real step between the sheet and what lies around it (along
    //     its whole length, either way round), its shape and size, how white and calm its inside is, whether its sides belong to a
    //     lattice of parallel lines (a cloth, tiles) - and a linear model (MODEL, fitted on a few thousand synthetic photographs, see
    //     tools/scanner-bench) turns them into one score. LEVELS are the thresholds of the three sensitivities;
    //  5. choice: the best score wins. The outline found a moment ago (hint) gets a bonus so that it does not jump between two
    //     sheets; and when two outlines are nested and what lies between them is a thin dark strip (a band, a printed frame) that
    //     grows out of the inner one, the outer one is taken: the strip is part of the sheet.

    var TAU = Math.PI * 2;
    var NT = 180;                // Hough angle bins around the full circle (2 degrees each); the circle keeps "bright side" apart
    var EDGE_FLOOR = 1.2;        // weakest gradient (grey levels per pixel) that is called an edge
    var COVER_STEP = 5;          // grey levels of step across a side that count as "there is an edge here"

    // Sensitivity 0 = strict (only a clear sheet), 1 = balanced, 2 = lenient (finds more, also false ones).
    var LEVELS = [{ minScore: 1 }, { minScore: -1 }, { minScore: -2.5 }];

    // The picture at a size the analysis can afford: its longer side is `side` pixels (so a phone held upright costs no more than one lying down).
    function analysisImage(source, side) {
        var sw = source.videoWidth || source.naturalWidth || source.width;
        var sh = source.videoHeight || source.naturalHeight || source.height;
        var k = Math.min(1, side / Math.max(sw, sh)), tw = Math.max(1, Math.round(sw * k)), th = Math.max(1, Math.round(sh * k));
        var cur = source, cw = sw, ch = sh, c, ctx;
        // halve step by step: a single big reduction would skip pixels and leave moire on fine patterns
        while (cw >= tw * 2 && ch >= th * 2) {
            var nw = Math.max(tw, Math.floor(cw / 2)), nh = Math.max(th, Math.floor(ch / 2));
            c = makeCanvas(nw, nh); ctx = c.getContext("2d"); ctx.imageSmoothingQuality = "high"; ctx.drawImage(cur, 0, 0, nw, nh);
            cur = c; cw = nw; ch = nh;
        }
        c = makeCanvas(tw, th); ctx = c.getContext("2d", { willReadFrequently: true }); ctx.imageSmoothingQuality = "high";
        ctx.drawImage(cur, 0, 0, tw, th);
        return ctx.getImageData(0, 0, tw, th);
    }

    // Grey (L), "whiteness" (M = darkest channel: high only for bright, neutral things), colourfulness (S), and J = their mix for edges.
    function paperPlanes(img) {
        var d = img.data, n = img.width * img.height, L = new Float32Array(n), M = new Float32Array(n), S = new Float32Array(n), J = new Float32Array(n);
        for (var i = 0, p = 0; i < n; i++, p += 4) {
            var r = d[p], g = d[p + 1], b = d[p + 2];
            var mx = r > g ? (r > b ? r : b) : (g > b ? g : b), mn = r < g ? (r < b ? r : b) : (g < b ? g : b);
            L[i] = 0.299 * r + 0.587 * g + 0.114 * b; M[i] = mn; S[i] = mx - mn; J[i] = 0.5 * L[i] + 0.5 * mn;
        }
        return { L: L, M: M, S: S, J: J };
    }

    function bilinear(a, w, h, x, y) {
        if (x < 0) { x = 0; } else if (x > w - 1.001) { x = w - 1.001; }
        if (y < 0) { y = 0; } else if (y > h - 1.001) { y = h - 1.001; }
        var x0 = x | 0, y0 = y | 0, fx = x - x0, fy = y - y0, i = y0 * w + x0;
        return a[i] * (1 - fx) * (1 - fy) + a[i + 1] * fx * (1 - fy) + a[i + w] * (1 - fx) * fy + a[i + w + 1] * fx * fy;
    }

    // Gradient (Sobel, grey levels per pixel) and the thin ridge of it (non-maximum suppression) = the edge pixels.
    function edgeField(J, w, h) {
        var n = w * h, gx = new Float32Array(n), gy = new Float32Array(n), mag = new Float32Array(n), x, y, i;
        for (y = 1; y < h - 1; y++) {
            for (x = 1; x < w - 1; x++) {
                i = y * w + x;
                var a = J[i - w - 1], b = J[i - w], c = J[i - w + 1], d = J[i - 1], f = J[i + 1], g = J[i + w - 1], hh = J[i + w], k = J[i + w + 1];
                var sx = (c + 2 * f + k - a - 2 * d - g) / 8, sy = (g + 2 * hh + k - a - 2 * b - c) / 8;
                gx[i] = sx; gy[i] = sy; mag[i] = Math.sqrt(sx * sx + sy * sy);
            }
        }
        var keep = new Uint8Array(n);
        for (y = 2; y < h - 2; y++) {
            for (x = 2; x < w - 2; x++) {
                i = y * w + x;
                var m = mag[i];
                if (m < EDGE_FLOOR) { continue; }
                var ax = Math.abs(gx[i]), ay = Math.abs(gy[i]), n1, n2;
                if (ay <= ax * 0.4142) { n1 = i - 1; n2 = i + 1; }
                else if (ax <= ay * 0.4142) { n1 = i - w; n2 = i + w; }
                else if ((gx[i] > 0) === (gy[i] > 0)) { n1 = i - w - 1; n2 = i + w + 1; }
                else { n1 = i - w + 1; n2 = i + w - 1; }
                if (m >= mag[n1] && m > mag[n2]) { keep[i] = 1; }
            }
        }
        return { gx: gx, gy: gy, mag: mag, keep: keep };
    }

    // The long straight edges of the picture: { nx, ny, rho } with nx*x + ny*y = rho, the normal pointing to the brighter side.
    function houghLines(ef, w, h, maxLines) {
        var cx = w / 2, cy = h / 2, D = Math.ceil(Math.hypot(w, h) / 2) + 2, NR = 2 * D + 1, step = TAU / NT;
        var acc = new Float32Array(NT * NR), cs = new Float32Array(NT), sn = new Float32Array(NT), k, i, r, o;
        for (k = 0; k < NT; k++) { cs[k] = Math.cos(k * step); sn[k] = Math.sin(k * step); }
        var count = 0;
        for (i = 0; i < ef.keep.length; i++) { if (ef.keep[i]) { count++; } }
        var ex = new Float32Array(count), ey = new Float32Array(count), ea = new Float32Array(count), em = new Float32Array(count), n = 0;
        for (i = 0; i < ef.keep.length; i++) {
            if (!ef.keep[i]) { continue; }
            var px = i % w, py = (i - px) / w, x = px - cx, y = py - cy, m = ef.mag[i];
            var th = Math.atan2(ef.gy[i], ef.gx[i]); if (th < 0) { th += TAU; }
            ex[n] = px; ey[n] = py; ea[n] = th; em[n] = m; n++;
            var kc = th / step, k0 = Math.round(kc), wt = Math.sqrt(m);
            for (var dk = -2; dk <= 2; dk++) {
                var f = 1 - Math.abs(kc - (k0 + dk)) / 2.6;
                if (f <= 0) { continue; }
                var kk = (k0 + dk + NT) % NT;
                acc[kk * NR + Math.round(x * cs[kk] + y * sn[kk]) + D] += wt * f;
            }
        }
        // smooth a little along rho and theta, then pick the peaks
        var tmp = new Float32Array(acc.length), sm = new Float32Array(acc.length), maxv = 0;
        for (k = 0; k < NT; k++) { for (r = 1; r < NR - 1; r++) { o = k * NR + r; tmp[o] = 0.25 * acc[o - 1] + 0.5 * acc[o] + 0.25 * acc[o + 1]; } }
        for (k = 0; k < NT; k++) {
            var kp = ((k + NT - 1) % NT) * NR, kn = ((k + 1) % NT) * NR, ko = k * NR;
            for (r = 0; r < NR; r++) { var v = 0.25 * tmp[kp + r] + 0.5 * tmp[ko + r] + 0.25 * tmp[kn + r]; sm[ko + r] = v; if (v > maxv) { maxv = v; } }
        }
        var thr = Math.max(0.2 * Math.min(w, h), 0.1 * maxv), cand = [];
        for (k = 0; k < NT; k++) {
            var kpp = ((k + NT - 1) % NT) * NR, knn = ((k + 1) % NT) * NR, koo = k * NR;
            for (r = 1; r < NR - 1; r++) {
                var val = sm[koo + r];
                if (val < thr) { continue; }
                if (val >= sm[koo + r - 1] && val >= sm[koo + r + 1] && val >= sm[kpp + r] && val >= sm[knn + r] && val >= sm[kpp + r - 1] && val >= sm[kpp + r + 1] && val >= sm[knn + r - 1] && val >= sm[knn + r + 1]) { cand.push([val, k, r]); }
            }
        }
        cand.sort(function (a, b) { return b[0] - a[0]; });
        var picked = [];
        for (i = 0; i < cand.length && picked.length < maxLines * 2; i++) {
            var c0 = cand[i], clash = false;
            for (var q = 0; q < picked.length; q++) {
                var dkk = Math.abs(picked[q][1] - c0[1]); if (dkk > NT / 2) { dkk = NT - dkk; }
                if (dkk <= 3 && Math.abs(picked[q][2] - c0[2]) <= 7) { clash = true; break; }
            }
            if (!clash) { picked.push(c0); }
        }

        // exact line from the edge pixels that belong to a peak (weighted total least squares)
        function fit(theta, rhoAbs) {
            for (var it = 0; it < 3; it++) {
                var nx = Math.cos(theta), ny = Math.sin(theta), sw = 0, sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0, cnt = 0;
                for (var j = 0; j < n; j++) {
                    var dist = ex[j] * nx + ey[j] * ny - rhoAbs;
                    if (dist > 2.5 || dist < -2.5) { continue; }
                    var da = ea[j] - theta; da -= TAU * Math.round(da / TAU);
                    if (da > 0.26 || da < -0.26) { continue; }
                    var wj = Math.sqrt(em[j]);
                    sw += wj; sx += wj * ex[j]; sy += wj * ey[j]; sxx += wj * ex[j] * ex[j]; sxy += wj * ex[j] * ey[j]; syy += wj * ey[j] * ey[j]; cnt++;
                }
                if (cnt < 8) { return null; }
                var mx = sx / sw, my = sy / sw, vxx = sxx / sw - mx * mx, vxy = sxy / sw - mx * my, vyy = syy / sw - my * my;
                var phi = 0.5 * Math.atan2(2 * vxy, vxx - vyy), nnx = -Math.sin(phi), nny = Math.cos(phi);
                if (nnx * nx + nny * ny < 0) { nnx = -nnx; nny = -nny; }
                theta = Math.atan2(nny, nnx); rhoAbs = nnx * mx + nny * my;
                if (it === 2) { return { nx: nnx, ny: nny, rho: rhoAbs, weight: sw, count: cnt }; }
            }
            return null;
        }
        var lines = [];
        for (i = 0; i < picked.length && lines.length < maxLines; i++) {
            var theta0 = picked[i][1] * step, rhoC = picked[i][2] - D;
            var line = fit(theta0, rhoC + cx * Math.cos(theta0) + cy * Math.sin(theta0));
            if (!line) { continue; }
            var dup = false;
            for (var z = 0; z < lines.length; z++) {
                if (lines[z].nx * line.nx + lines[z].ny * line.ny > 0.998 && Math.abs(lines[z].rho - line.rho) < 2.5) { dup = true; break; }
            }
            if (!dup) { lines.push(line); }
        }
        return lines;
    }

    // How a line looks along its length inside the picture: at every pixel step the difference between what is a few pixels to
    // one side and to the other (bright-to-the-+n-side steps counted in cp/ep, bright-to-the--n-side in cm/em; prefix sums).
    function lineProfile(S, L) {
        var w = S.w, h = S.h, ux = -L.ny, uy = L.nx, ox = L.nx * L.rho, oy = L.ny * L.rho, lo = -1e9, hi = 1e9;
        function clip(o, u, max) {
            if (Math.abs(u) < 1e-9) { if (o < 1 || o > max) { lo = 1; hi = 0; } return; }
            var a = (1 - o) / u, b = (max - o) / u;
            if (a > b) { var t = a; a = b; b = t; }
            if (a > lo) { lo = a; } if (b < hi) { hi = b; }
        }
        clip(ox, ux, w - 2); clip(oy, uy, h - 2);
        if (hi - lo < 6) { return null; }
        var nt = Math.floor(hi - lo) + 1, cp = new Float32Array(nt + 1), ep = new Float32Array(nt + 1), cm = new Float32Array(nt + 1), em = new Float32Array(nt + 1), sp = new Float32Array(nt + 1), sq = new Float32Array(nt + 1);
        var J = S.J, nx = L.nx, ny = L.ny, offs = [2.5, 4, 5.5];
        for (var i = 0; i < nt; i++) {
            var t = lo + i, px = ox + t * ux, py = oy + t * uy, a = 0, b = 0;
            for (var k = 0; k < 3; k++) {
                a += bilinear(J, w, h, px + offs[k] * nx, py + offs[k] * ny);
                b += bilinear(J, w, h, px - offs[k] * nx, py - offs[k] * ny);
            }
            var step = (a - b) / 3;
            cp[i + 1] = cp[i] + (step >= COVER_STEP ? 1 : 0); ep[i + 1] = ep[i] + (step > 0 ? Math.min(step, 60) : 0);
            cm[i + 1] = cm[i] + (step <= -COVER_STEP ? 1 : 0); em[i + 1] = em[i] + (step < 0 ? Math.min(-step, 60) : 0);
            var cs = Math.max(-60, Math.min(60, step)); sp[i + 1] = sp[i] + cs; sq[i + 1] = sq[i] + cs * cs;
        }
        return { t0: lo, nt: nt, ox: ox, oy: oy, ux: ux, uy: uy, cp: cp, ep: ep, cm: cm, em: em, sp: sp, sq: sq };
    }

    function lineThrough(p, q) { // inward normal for a polygon that runs clockwise on the screen
        var dx = q[0] - p[0], dy = q[1] - p[1], len = Math.hypot(dx, dy) || 1, nx = -dy / len, ny = dx / len;
        return { nx: nx, ny: ny, rho: nx * p[0] + ny * p[1] };
    }

    function sat(x) { return x < 0 ? 0 : (x > 1 ? 1 : x); }

    // The sides of the quadrilateral V (corners clockwise; side k lies on lineOf(k)): for each, how much of it is a step from a
    // brighter inside (b) or a darker inside (d) to the outside, and how much of it is in the picture.
    function sideEvidence(S, V, lineOf, medOf) {
        var cxq = (V[0][0] + V[1][0] + V[2][0] + V[3][0]) / 4, cyq = (V[0][1] + V[1][1] + V[2][1] + V[3][1]) / 4;
        var out = { b: [], d: [], vis: [], tb: [], td: [] };
        for (var k = 0; k < 4; k++) {
            var L = lineOf(k), P = V[k], Q = V[(k + 1) % 4];
            if (L.frame) {
                // the sheet goes on beyond the picture here: is what lies just inside along this side like the inside of the sheet?
                var same = 0, cntF = 0, med = medOf();
                for (var tf = 0.04; tf < 0.97; tf += 0.04) {
                    var mf = bilinear(S.M, S.w, S.h, P[0] + (Q[0] - P[0]) * tf + L.nx * 2.5, P[1] + (Q[1] - P[1]) * tf + L.ny * 2.5);
                    cntF++; if (Math.abs(mf - med) <= 14 + 0.12 * med) { same++; }
                }
                out.b.push(same / cntF); out.d.push(same / cntF); out.tb.push(same / cntF); out.td.push(same / cntF); out.vis.push(1); out.nFrame = (out.nFrame || 0) + 1; out.contSum = (out.contSum || 0) + same / cntF; continue;
            }
            var prof = L.prof === undefined ? (L.prof = lineProfile(S, L)) : L.prof;
            if (!prof) { return null; }
            var inPlus = L.nx * cxq + L.ny * cyq > L.rho;
            // leave out the ends: next to a corner the other side's edge gets into the bands
            var ax = P[0] + 0.05 * (Q[0] - P[0]), ay = P[1] + 0.05 * (Q[1] - P[1]), bx = P[0] + 0.95 * (Q[0] - P[0]), by = P[1] + 0.95 * (Q[1] - P[1]);
            var ta = (ax - prof.ox) * prof.ux + (ay - prof.oy) * prof.uy, tb = (bx - prof.ox) * prof.ux + (by - prof.oy) * prof.uy;
            var lo = Math.min(ta, tb), hi = Math.max(ta, tb), full = Math.max(1, hi - lo);
            var i0 = Math.max(0, Math.ceil(lo - prof.t0)), i1 = Math.min(prof.nt - 1, Math.floor(hi - prof.t0)), cnt = i1 - i0 + 1;
            if (cnt < 4) { return null; }
            var vis = cnt / full;
            if (vis < 0.3) { return null; }
            var cB = inPlus ? prof.cp : prof.cm, eB = inPlus ? prof.ep : prof.em, cD = inPlus ? prof.cm : prof.cp, eD = inPlus ? prof.em : prof.ep;
            var visK = vis < 0.9 ? 0.6 + 0.4 * vis / 0.9 : 1;
            out.b.push((cB[i1 + 1] - cB[i0]) / cnt * (0.35 + 0.65 * sat((eB[i1 + 1] - eB[i0]) / cnt / 15)) * visK);
            out.d.push((cD[i1 + 1] - cD[i0]) / cnt * (0.35 + 0.65 * sat((eD[i1 + 1] - eD[i0]) / cnt / 15)) * visK);
            var sm = (prof.sp[i1 + 1] - prof.sp[i0]) / cnt, sv = Math.max(0, (prof.sq[i1 + 1] - prof.sq[i0]) / cnt - sm * sm), tt = (inPlus ? sm : -sm) / (Math.sqrt(sv) + 1) / 4;
            out.tb.push(sat(tt)); out.td.push(sat(-tt));
            out.vis.push(vis);
        }
        return out;
    }

    function gridAt(V, u, v) {
        return [(1 - v) * ((1 - u) * V[0][0] + u * V[1][0]) + v * ((1 - u) * V[3][0] + u * V[2][0]), (1 - v) * ((1 - u) * V[0][1] + u * V[1][1]) + v * ((1 - u) * V[3][1] + u * V[2][1])];
    }

    // What the inside of V looks like: white / calm / busy, and how much brighter it is than its surroundings.
    function insideLook(S, V) {
        var g = 8, sumM = 0, sumS = 0, sumJ = 0, sumD = 0, n = 0, vals = [], a, b, p;
        for (a = 0; a < g; a++) {
            for (b = 0; b < g; b++) {
                p = gridAt(V, (a + 0.5) / g, (b + 0.5) / g);
                var m = bilinear(S.M, S.w, S.h, p[0], p[1]);
                sumM += m; sumS += bilinear(S.S, S.w, S.h, p[0], p[1]); sumJ += bilinear(S.J, S.w, S.h, p[0], p[1]); sumD += bilinear(S.dens, S.w, S.h, p[0], p[1]); vals.push(m); n++;
            }
        }
        vals.sort(function (x, y) { return x - y; });
        var med = vals[n >> 1], close = 0, rank = 0;
        if (S.mCum) { rank = S.mCum[Math.max(0, Math.min(255, Math.round(med)))]; }
        for (a = 0; a < n; a++) { if (Math.abs(vals[a] - med) <= 14 + 0.12 * med) { close++; } }
        // outside: a few steps beyond the middle of every side
        var outJ = 0, outM = 0, outS = 0, outN = 0;
        for (var k = 0; k < 4; k++) {
            var P = V[k], Q = V[(k + 1) % 4], dx = Q[0] - P[0], dy = Q[1] - P[1], len = Math.hypot(dx, dy) || 1, nx = dy / len, ny = -dx / len;
            for (var f = 0.25; f < 0.8; f += 0.25) {
                var ox = P[0] + dx * f + nx * 6, oy = P[1] + dy * f + ny * 6;
                if (ox < 0 || oy < 0 || ox > S.w - 1 || oy > S.h - 1) { continue; }
                outJ += bilinear(S.J, S.w, S.h, ox, oy); outM += bilinear(S.M, S.w, S.h, ox, oy); outS += bilinear(S.S, S.w, S.h, ox, oy); outN++;
            }
        }
        return { med: med, white: sumM / n, calm: close / n, plain: sumS / n, busy: sumD / n, ring: outN ? sumJ / n - outJ / outN : 0, ringM: outN ? sumM / n - outM / outN : 0, ringS: outN ? sumS / n - outS / outN : 0, rank: rank, outN: outN };
    }

    function shapeOf(V) {
        var rect = rectangularity(V);
        var w = (sideLength(V[0], V[1]) + sideLength(V[3], V[2])) / 2, h = (sideLength(V[0], V[3]) + sideLength(V[1], V[2])) / 2;
        return { rect: rect, ratio: Math.max(w, h) / Math.max(1, Math.min(w, h)) };
    }

    // All sets of four (oriented) lines that could bound a sheet: inward normals turning clockwise by about 90 degrees each time.
    function lineQuads(S, lines, out, limit) {
        var ol = [], i, j, k, l;
        lines.forEach(function (L, n) { ol.push({ L: L, idx: n, nx: L.nx, ny: L.ny, rho: L.rho }); ol.push({ L: L, idx: n, nx: -L.nx, ny: -L.ny, rho: -L.rho }); });
        // the border of the picture: a sheet that goes on beyond it has a side there (nothing to see, so it counts as neutral)
        S.frameLines.forEach(function (L, n) { ol.push({ L: L, idx: lines.length + n, frame: true, nx: L.nx, ny: L.ny, rho: L.rho }); });
        var K = ol.length, phi = [];
        for (i = 0; i < K; i++) { var p = Math.atan2(ol[i].ny, ol[i].nx); phi[i] = p < 0 ? p + TAU : p; }
        function rel(a, b) { var d = phi[b] - phi[a]; return d < 0 ? d + TAU : d; }
        var A0 = 0.96, A1 = 2.18, O0 = 2.62, O1 = 3.67;
        function isect(La, Lb) {
            var det = La.nx * Lb.ny - La.ny * Lb.nx;
            if (Math.abs(det) < 0.15) { return null; }
            return [(La.rho * Lb.ny - La.ny * Lb.rho) / det, (La.nx * Lb.rho - La.rho * Lb.nx) / det];
        }
        var margin = 0.3, frameArea = S.w * S.h;
        for (i = 0; i < K; i++) {
            for (j = 0; j < K; j++) {
                if (ol[j].idx <= ol[i].idx) { continue; }
                var d12 = rel(i, j); if (d12 < A0 || d12 > A1) { continue; }
                var P12 = isect(ol[i], ol[j]); if (!P12) { continue; }
                for (k = 0; k < K; k++) {
                    if (ol[k].idx <= ol[i].idx || ol[k].idx === ol[j].idx) { continue; }
                    var d23 = rel(j, k), d13 = rel(i, k);
                    if (d23 < A0 || d23 > A1 || d13 < O0 || d13 > O1) { continue; }
                    var P23 = isect(ol[j], ol[k]); if (!P23) { continue; }
                    for (l = 0; l < K; l++) {
                        if (ol[l].idx <= ol[i].idx || ol[l].idx === ol[j].idx || ol[l].idx === ol[k].idx) { continue; }
                        if ((ol[i].frame ? 1 : 0) + (ol[j].frame ? 1 : 0) + (ol[k].frame ? 1 : 0) + (ol[l].frame ? 1 : 0) > 2) { continue; }
                        var d34 = rel(k, l), d41 = rel(l, i), d24 = rel(j, l);
                        if (d34 < A0 || d34 > A1 || d41 < A0 || d41 > A1 || d24 < O0 || d24 > O1) { continue; }
                        var P34 = isect(ol[k], ol[l]), P41 = isect(ol[l], ol[i]);
                        if (!P34 || !P41) { continue; }
                        var V = [P41, P12, P23, P34], ok = true, c;
                        for (c = 0; c < 4 && ok; c++) {
                            var a = V[c], b = V[(c + 1) % 4], d = V[(c + 2) % 4];
                            if ((b[0] - a[0]) * (d[1] - b[1]) - (b[1] - a[1]) * (d[0] - b[0]) <= 0) { ok = false; }
                            if (a[0] < -margin * S.w || a[0] > (1 + margin) * S.w || a[1] < -margin * S.h || a[1] > (1 + margin) * S.h) { ok = false; }
                        }
                        if (!ok) { continue; }
                        var area = polygonArea(V);
                        if (area < frameArea * 0.03 || area > frameArea * 1.6) { continue; }
                        var qx = (V[0][0] + V[1][0] + V[2][0] + V[3][0]) / 4, qy = (V[0][1] + V[1][1] + V[2][1] + V[3][1]) / 4;
                        if (ol[i].nx * qx + ol[i].ny * qy <= ol[i].rho || ol[j].nx * qx + ol[j].ny * qy <= ol[j].rho || ol[k].nx * qx + ol[k].ny * qy <= ol[k].rho || ol[l].nx * qx + ol[l].ny * qy <= ol[l].rho) { continue; }
                        out.push({ V: V, lines: [ol[i].L, ol[j].L, ol[k].L, ol[l].L], source: "lines" });
                        if (out.length > limit) { return; }
                    }
                }
            }
        }
    }

    // The part of polygon `subject` that lies inside the convex polygon `clip` (both clockwise on the screen).
    function clipConvex(subject, clip) {
        var poly = subject;
        for (var e = 0; e < clip.length && poly.length; e++) {
            var a = clip[e], b = clip[(e + 1) % clip.length], input = poly;
            poly = [];
            var inside = function (p) { return (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0]) >= 0; };
            var inter = function (p, q) {
                var den = (p[0] - q[0]) * (a[1] - b[1]) - (p[1] - q[1]) * (a[0] - b[0]);
                if (Math.abs(den) < 1e-12) { return q; }
                var t = ((p[0] - a[0]) * (a[1] - b[1]) - (p[1] - a[1]) * (a[0] - b[0])) / den;
                return [p[0] + t * (q[0] - p[0]), p[1] + t * (q[1] - p[1])];
            };
            for (var j = 0; j < input.length; j++) {
                var cur = input[j], prev = input[(j + input.length - 1) % input.length];
                if (inside(cur)) { if (!inside(prev)) { poly.push(inter(prev, cur)); } poly.push(cur); } else if (inside(prev)) { poly.push(inter(prev, cur)); }
            }
        }
        return poly;
    }

    function polygonIou(p, q) {
        var inter = clipConvex(p, q), ia = inter.length >= 3 ? polygonArea(inter) : 0, ua = polygonArea(p) + polygonArea(q) - ia;
        return ua > 0 ? ia / ua : 0;
    }

    // Everything a candidate is judged by, as numbers.
    function describe(S, c, medOf, allLines) {
        var lineOf;
        if (c.lines) { lineOf = function (k) { return c.lines[k]; }; }
        else { var made = [0, 1, 2, 3].map(function (k) { return lineThrough(c.V[k], c.V[(k + 1) % 4]); }); lineOf = function (k) { return made[k]; }; }
        var ev = sideEvidence(S, c.V, lineOf, medOf);
        if (!ev) { return null; }
        var f = {}, k, mix = [], b = ev.b.slice().sort(function (x, y) { return x - y; });
        for (k = 0; k < 4; k++) { mix.push(Math.max(ev.b[k], 0.8 * ev.d[k])); }
        var m = mix.slice().sort(function (x, y) { return x - y; });
        f.bMin = b[0]; f.bMean = (b[0] + b[1] + b[2] + b[3]) / 4; f.mMin = m[0]; f.mMean = (m[0] + m[1] + m[2] + m[3]) / 4;
        f.dark = 0; for (k = 0; k < 4; k++) { if (ev.d[k] > ev.b[k]) { f.dark++; } }
        var tm = []; for (k = 0; k < 4; k++) { tm.push(Math.max(ev.tb[k], 0.8 * ev.td[k])); }
        tm.sort(function (x, y) { return x - y; }); f.tMin = tm[0]; f.tMean = (tm[0] + tm[1] + tm[2] + tm[3]) / 4;
        f.vis = Math.min.apply(null, ev.vis); f.nFrame = ev.nFrame || 0;
        // a side on the border of the picture: the sheet goes on beyond it (how sure - from what lies just inside - is the second number)
        f.frameCont = f.nFrame ? f.nFrame / 2 * (ev.contSum / f.nFrame) : 0; f.frameWeak = f.nFrame ? f.nFrame / 2 * (1 - ev.contSum / f.nFrame) : 0;
        // a grid or a tablecloth has many lines as strong as the sides, running the same way; a sheet stands alone
        f.grid = 0;
        if (c.lines && allLines) {
            var minW = Infinity, own = c.lines;
            for (k = 0; k < 4; k++) { if (!own[k].frame && own[k].weight < minW) { minW = own[k].weight; } }
            if (minW < Infinity) {
                for (var q = 0; q < allLines.length; q++) {
                    var Lo = allLines[q];
                    if (own[0] === Lo || own[1] === Lo || own[2] === Lo || own[3] === Lo || Lo.weight < 0.4 * minW) { continue; }
                    for (k = 0; k < 2; k++) {
                        if (own[k].frame) { continue; }
                        if (Math.abs(own[k].nx * Lo.ny - own[k].ny * Lo.nx) < 0.09) { f.grid++; break; }
                    }
                }
            }
        }
        // sides that belong to a family of several parallel lines of about the same strength: they lie in a lattice (a cloth, tiles)
        f.perSides = 0; f.perMean = 0;
        if (c.lines && allLines) {
            var cnt = 0, sum = 0, many = 0;
            for (k = 0; k < 4; k++) {
                var Ls = c.lines[k];
                if (Ls.frame) { continue; }
                if (Ls.par === undefined) {
                    Ls.par = 0;
                    for (var q2 = 0; q2 < allLines.length; q2++) {
                        var Lp = allLines[q2];
                        if (Lp !== Ls && Lp.weight >= 0.5 * Ls.weight && Math.abs(Ls.nx * Lp.ny - Ls.ny * Lp.nx) < 0.07) { Ls.par++; }
                    }
                }
                cnt++; sum += Math.min(Ls.par, 6) / 6; if (Ls.par >= 3) { many++; }
            }
            if (cnt) { f.perSides = many / cnt; f.perMean = sum / cnt; }
        }
        return f;
    }

    // How much of the line L between points P and Q is a step (either way) - 0..1
    function lineCoverage(S, L, P, Q) {
        var prof = L.prof === undefined ? (L.prof = lineProfile(S, L)) : L.prof;
        if (!prof) { return 0; }
        var ta = (P[0] - prof.ox) * prof.ux + (P[1] - prof.oy) * prof.uy, tb = (Q[0] - prof.ox) * prof.ux + (Q[1] - prof.oy) * prof.uy;
        var lo = Math.min(ta, tb), hi = Math.max(ta, tb), i0 = Math.max(0, Math.ceil(lo - prof.t0)), i1 = Math.min(prof.nt - 1, Math.floor(hi - prof.t0)), cnt = i1 - i0 + 1;
        if (cnt < 3) { return 0; }
        return Math.max((prof.cp[i1 + 1] - prof.cp[i0]) / cnt, (prof.cm[i1 + 1] - prof.cm[i0]) / cnt);
    }

    function insideConvex(poly, p, tol) { // clockwise polygon
        for (var i = 0; i < poly.length; i++) {
            var a = poly[i], b = poly[(i + 1) % poly.length];
            if ((b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0]) < -tol) { return false; }
        }
        return true;
    }

    // A sheet with a dark band or a printed frame has two outlines: the sheet, and what lies inside the band. How dark is what
    // lies between inner quadrilateral A and outer quadrilateral B (0 = as light as the sheet, 1 = black)? -1 = nothing between.
    function stripDarkness(S, A, B, whiteA) {
        var sum = 0, n = 0;
        for (var k = 0; k < 4; k++) {
            var P = A[k], Q = A[(k + 1) % 4], dx = Q[0] - P[0], dy = Q[1] - P[1], len = Math.hypot(dx, dy) || 1, nx = dy / len, ny = -dx / len;
            for (var f = 0.2; f < 0.85; f += 0.2) {
                for (var d = 3; d <= 9; d += 3) {
                    var x = P[0] + dx * f + nx * d, y = P[1] + dy * f + ny * d;
                    if (x < 0 || y < 0 || x > S.w - 1 || y > S.h - 1 || !insideConvex(B, [x, y], 0)) { continue; }
                    sum += bilinear(S.M, S.w, S.h, x, y); n++;
                }
            }
        }
        if (n < 8) { return -1; }
        return sat((whiteA - sum / n) / Math.max(50, 0.5 * whiteA));
    }

    // options: sensitivity (0 strict .. 2 lenient, default 1), hint (the quad found in the picture before: the outline sticks to it),
    // precise (a photograph that can be judged at leisure: a closer look), size (long side of the analysis copy).
    // Returns { quad, score, margin (score above the threshold), area (of the picture), cut (reaches beyond the picture) } or null.
    function detect(source, options) {
        var opts = options || {};
        var level = LEVELS[Math.max(0, Math.min(2, opts.sensitivity != null ? opts.sensitivity : 1))];
        var i;
        var img = analysisImage(source, opts.size || (opts.precise ? 520 : 400));
        var w = img.width, h = img.height, pl = paperPlanes(img);
        var J = boxBlur(pl.J, w, h, 1);
        var ef = edgeField(J, w, h);
        var strong = new Float32Array(w * h);
        for (i = 0; i < strong.length; i++) { strong[i] = ef.keep[i] && ef.mag[i] > 6 ? 1 : 0; }
        var hist = new Float32Array(256);
        for (i = 0; i < pl.M.length; i++) { hist[Math.min(255, Math.max(0, pl.M[i] | 0))]++; }
        for (i = 1; i < 256; i++) { hist[i] += hist[i - 1]; }
        for (i = 0; i < 256; i++) { hist[i] /= pl.M.length; }
        var S = { w: w, h: h, J: J, M: pl.M, S: pl.S, dens: boxBlur(strong, w, h, 3), mCum: hist };
        S.frameLines = [{ nx: 0, ny: 1, rho: -0.5, frame: true }, { nx: -1, ny: 0, rho: -(w - 0.5), frame: true }, { nx: 0, ny: -1, rho: -(h - 0.5), frame: true }, { nx: 1, ny: 0, rho: -0.5, frame: true }];
        var lines = houghLines(ef, w, h, opts.maxLines || 22);
        var cands = [];
        lineQuads(S, lines, cands, 60000);

        var scored = [];
        cands.forEach(function (c) {
            var look = null, f = describe(S, c, function () { look = look || insideLook(S, c.V); return look.med; }, lines);
            if (!f || f.mMean < 0.2) { return; }
            if (!look) { look = insideLook(S, c.V); }
            var area = Math.min(polygonArea(c.V), w * h) / (w * h), sh = shapeOf(c.V);
            f.area = area; f.rect = sh.rect; f.ratio = sh.ratio; f.white = look.white; f.calm = look.calm; f.plain = look.plain; f.busy = look.busy; f.ring = look.ring; f.ringM = look.ringM; f.ringS = look.ringS; f.rank = look.rank;
            scored.push({ V: c.V, f: f, lines: c.lines });
        });
        var hint = null, hx = 0, hy = 0, hArea = 0;
        if (opts.hint && opts.hint.length === 4) {
            hint = opts.hint.map(function (p) { return [p[0] * w - 0.5, p[1] * h - 0.5]; });
            hx = (hint[0][0] + hint[1][0] + hint[2][0] + hint[3][0]) / 4; hy = (hint[0][1] + hint[1][1] + hint[2][1] + hint[3][1]) / 4; hArea = polygonArea(hint);
        }
        scored.forEach(function (r) {
            r.score = rate(r.f);
            if (hint && hArea > 0) {
                var V = r.V, qx = (V[0][0] + V[1][0] + V[2][0] + V[3][0]) / 4, qy = (V[0][1] + V[1][1] + V[2][1] + V[3][1]) / 4, ratio = polygonArea(V) / hArea;
                if (Math.hypot(qx - hx, qy - hy) < 0.15 * Math.hypot(w, h) && ratio > 0.6 && ratio < 1.7) { var o = sat((polygonIou(V, hint) - 0.5) / 0.5); r.score += 2.5 * o * o; }
            }
        });
        scored.sort(function (a, b) { return b.score - a.score; });
        // Nested outlines: prefer the outer one when the thin strip between the two is dark (a band, a frame), because that is
        // part of the sheet - cutting it off would lose what is printed there.
        var top = scored.slice(0, 14), areas = top.map(function (r) { return polygonArea(r.V); });
        top.forEach(function (B, ib) {
            for (var ia = 0; ia < top.length; ia++) {
                var A = top[ia], ratio = areas[ib] / areas[ia];
                if (ia === ib || ratio < 1.03 || ratio > 1.45 || !A.lines || !B.lines) { continue; }
                if (A.score < level.minScore || B.score < A.score - 3.5 || B.f.mMean < 0.45 || B.f.nFrame > 0) { continue; }
                // the outer one must grow out of the inner one: two of its sides are the same lines, or all four run alongside (a frame)
                var shared = 0, alongside = 0, q, m;
                for (q = 0; q < 4; q++) {
                    for (m = 0; m < 4; m++) {
                        if (B.lines[q] === A.lines[m]) { shared++; }
                        else if (Math.abs(B.lines[q].nx * A.lines[m].ny - B.lines[q].ny * A.lines[m].nx) < 0.05 && B.lines[q].nx * A.lines[m].nx + B.lines[q].ny * A.lines[m].ny > 0) { alongside++; break; }
                    }
                }
                if (shared < 2 && alongside + shared < 4) { continue; }
                var grows = true;
                for (q = 0; q < 4 && grows; q++) {
                    for (m = 0; m < 4; m++) {
                        if (B.lines[q] !== A.lines[m] || B.lines[q].frame) { continue; }
                        // the part of this side of B that lies outside A: from A's corner to B's corner on that line (the far end)
                        var Pb = B.V[q], Qb = B.V[(q + 1) % 4], Pa = A.V[m], Qa = A.V[(m + 1) % 4];
                        var dPA = Math.hypot(Pb[0] - Pa[0], Pb[1] - Pa[1]) + Math.hypot(Qb[0] - Qa[0], Qb[1] - Qa[1]), dPQ = Math.hypot(Pb[0] - Qa[0], Pb[1] - Qa[1]) + Math.hypot(Qb[0] - Pa[0], Qb[1] - Pa[1]);
                        var e1 = dPA <= dPQ ? [Pb, Pa] : [Pb, Qa], e2 = dPA <= dPQ ? [Qb, Qa] : [Qb, Pa];
                        if (Math.hypot(e1[0][0] - e1[1][0], e1[0][1] - e1[1][1]) > 3 && lineCoverage(S, B.lines[q], e1[0], e1[1]) < 0.7) { grows = false; }
                        if (Math.hypot(e2[0][0] - e2[1][0], e2[0][1] - e2[1][1]) > 3 && lineCoverage(S, B.lines[q], e2[0], e2[1]) < 0.7) { grows = false; }
                    }
                }
                if (!grows) { continue; }
                var inside = true;
                for (var c = 0; c < 4 && inside; c++) { inside = insideConvex(B.V, A.V[c], 2.5); }
                if (!inside) { continue; }
                var sd = stripDarkness(S, A.V, B.V, A.f.white);
                if (sd >= 0.4) { B.bonus = Math.max(B.bonus || 0, 3.5 * sd); }
            }
        });
        top.forEach(function (r) { if (r.bonus) { r.score += r.bonus; } });
        scored.sort(function (a, b) { return b.score - a.score; });
        var best = null;
        for (i = 0; i < scored.length; i++) { if (scored[i].score >= level.minScore) { best = scored[i]; break; } }

        if (opts.debug) {
            return {
                w: w, h: h, lines: lines.map(function (l) { return { nx: l.nx, ny: l.ny, rho: l.rho, weight: l.weight }; }),
                candidates: scored.slice(0, opts.debugAll ? 100000 : 8).map(function (r) { return { f: r.f, score: r.score, V: r.V.map(function (p) { return [(p[0] + 0.5) / w, (p[1] + 0.5) / h]; }) }; }),
                best: !!best
            };
        }
        if (!best) { return null; }
        var V = best.V;
        var outside = V.some(function (p) { return p[0] < -0.5 || p[0] > w - 0.5 || p[1] < -0.5 || p[1] > h - 0.5; });
        if (outside) {
            var poly = clipConvex(V, [[0, 0], [w - 1, 0], [w - 1, h - 1], [0, h - 1]]);
            if (poly.length >= 4) { V = reduceToQuad(convexHull(poly.map(function (p) { return [p[0], p[1]]; }))); } else { return null; }
        }
        V = orderQuad(V);   // top left first, then clockwise: the straightened page stands up the way it was photographed
        return { quad: V.map(function (p) { return [Math.min(1, Math.max(0, (p[0] + 0.5) / w)), Math.min(1, Math.max(0, (p[1] + 0.5) / h))]; }), score: best.score, margin: best.score - level.minScore, area: best.f.area, cut: best.f.nFrame > 0 };
    }

    // How likely a candidate is a sheet, from its features: a linear model, fitted on synthetic photographs of sheets (tools/scanner-bench).
    // MODEL-BEGIN
    var MODEL = {
        names: ["tMean","tMin","mMean","mMin","bMean","bMin","dark","vis","dark1","dark2","dark3","area","areaSmall","rect","ratioHi","ratioLo","white","calm","plain","busy","ring","ringNeg","ringM","rank","ringS","grid","perSides","perMean","nFrame","frameCont","frameWeak"],
        w: [0.0189,1.513,4.5073,0.2513,1.2403,0.1776,1.1109,3.9866,-0.222,-0.7751,0.252,11.7193,-4.2279,3.2105,0.8394,-1.6338,2.7975,0.6182,2.283,2.1735,0.0259,0.6473,1.2,1.5968,-1.0822,-1.1285,-0.7987,-6.3208,-2.7257,-2.5863,-0.0509],
        b: -20.6496
    };
    // MODEL-END

    function rate(f) {
        var v = {
            tMean: f.tMean, tMin: f.tMin, mMean: f.mMean, mMin: f.mMin, bMean: f.bMean, bMin: f.bMin, dark: f.dark / 4, vis: f.vis,
            dark1: f.dark === 1 ? 1 : 0, dark2: f.dark === 2 ? 1 : 0, dark3: f.dark >= 3 ? 1 : 0,
            area: Math.sqrt(Math.min(1, f.area)), areaSmall: f.area < 0.1 ? 1 - f.area / 0.1 : 0,
            rect: f.rect, ratioHi: Math.max(0, Math.log(f.ratio / 2.1)), ratioLo: f.ratio < 1.12 ? 1 : 0,
            white: sat((f.white - 60) / 120), calm: f.calm, plain: 1 - sat(f.plain / 80), busy: Math.min(1, f.busy * 4),
            ring: Math.max(-1, Math.min(1, f.ring / 40)), ringNeg: f.ring < 0 ? 1 : 0, ringM: Math.max(-1, Math.min(1, f.ringM / 60)),
            rank: f.rank, ringS: Math.max(-1, Math.min(1, f.ringS / 60)),
            grid: Math.min(f.grid, 6) / 6, perSides: f.perSides, perMean: f.perMean, nFrame: (f.nFrame || 0) / 2, frameCont: f.frameCont, frameWeak: f.frameWeak
        };
        var z = MODEL.b;
        for (var j = 0; j < MODEL.names.length; j++) { z += MODEL.w[j] * v[MODEL.names[j]]; }
        return z;
    }

    // ------------------------------------------------------------------ warp

    // Homography that maps the unit rectangle (0,0)-(w,h) onto the four source points.
    function homography(dst, src) {
        // unknowns h0..h7 (h8 = 1); 8 equations from 4 point pairs
        var A = [], b = [], i;
        for (i = 0; i < 4; i++) {
            var x = dst[i][0], y = dst[i][1], u = src[i][0], v = src[i][1];
            A.push([x, y, 1, 0, 0, 0, -u * x, -u * y]); b.push(u);
            A.push([0, 0, 0, x, y, 1, -v * x, -v * y]); b.push(v);
        }
        for (var c = 0; c < 8; c++) {
            var pivot = c;
            for (var r = c + 1; r < 8; r++) { if (Math.abs(A[r][c]) > Math.abs(A[pivot][c])) { pivot = r; } }
            var tmp = A[c]; A[c] = A[pivot]; A[pivot] = tmp;
            var tb = b[c]; b[c] = b[pivot]; b[pivot] = tb;
            for (var r2 = c + 1; r2 < 8; r2++) {
                var f = A[r2][c] / A[c][c];
                for (var k = c; k < 8; k++) { A[r2][k] -= f * A[c][k]; }
                b[r2] -= f * b[c];
            }
        }
        var hh = new Array(8);
        for (var r3 = 7; r3 >= 0; r3--) {
            var s = b[r3];
            for (var k2 = r3 + 1; k2 < 8; k2++) { s -= A[r3][k2] * hh[k2]; }
            hh[r3] = s / A[r3][r3];
        }
        return hh;
    }

    function dist(a, b) { return Math.hypot(a[0] - b[0], a[1] - b[1]); }

    function warp(source, quad, maxSide) {
        var sw = source.width, sh = source.height;
        var pts = quad.map(function (p) { return [p[0] * sw, p[1] * sh]; });
        var width = (dist(pts[0], pts[1]) + dist(pts[3], pts[2])) / 2;
        var height = (dist(pts[0], pts[3]) + dist(pts[1], pts[2])) / 2;
        var scale = Math.min(1, (maxSide || 2200) / Math.max(width, height));
        var ow = Math.max(2, Math.round(width * scale)), oh = Math.max(2, Math.round(height * scale));

        var srcData = source.getContext("2d", { willReadFrequently: true }).getImageData(0, 0, sw, sh).data;
        var out = makeCanvas(ow, oh), octx = out.getContext("2d", { willReadFrequently: true });
        var dstImg = octx.createImageData(ow, oh), dd = dstImg.data;
        var H = homography([[0, 0], [ow, 0], [ow, oh], [0, oh]], pts);

        for (var y = 0; y < oh; y++) {
            for (var x = 0; x < ow; x++) {
                var px = x + 0.5, py = y + 0.5;
                var den = H[6] * px + H[7] * py + 1;
                var u = (H[0] * px + H[1] * py + H[2]) / den - 0.5;
                var v = (H[3] * px + H[4] * py + H[5]) / den - 0.5;
                var o = (y * ow + x) * 4;
                if (u < 0 || v < 0 || u > sw - 1 || v > sh - 1) { dd[o] = dd[o + 1] = dd[o + 2] = 255; dd[o + 3] = 255; continue; }
                var x0 = u | 0, y0 = v | 0, fx = u - x0, fy = v - y0;
                var x1 = Math.min(sw - 1, x0 + 1), y1 = Math.min(sh - 1, y0 + 1);
                var i00 = (y0 * sw + x0) * 4, i10 = (y0 * sw + x1) * 4, i01 = (y1 * sw + x0) * 4, i11 = (y1 * sw + x1) * 4;
                for (var ch = 0; ch < 3; ch++) {
                    dd[o + ch] = srcData[i00 + ch] * (1 - fx) * (1 - fy) + srcData[i10 + ch] * fx * (1 - fy)
                        + srcData[i01 + ch] * (1 - fx) * fy + srcData[i11 + ch] * fx * fy;
                }
                dd[o + 3] = 255;
            }
        }
        octx.putImageData(dstImg, 0, 0);
        return out;
    }

    // ------------------------------------------------------------------ filters

    function copyCanvas(src) {
        var c = makeCanvas(src.width, src.height);
        c.getContext("2d", { willReadFrequently: true }).drawImage(src, 0, 0);
        return c;
    }

    // Evens out the lighting: every pixel is divided by the local paper brightness.
    function backgroundOf(gray, w, h) {
        var r = Math.max(8, Math.round(Math.max(w, h) / 24));
        // blur a small copy: the background is smooth, and this keeps big photos fast
        var step = Math.max(1, Math.floor(Math.max(w, h) / 400));
        if (step === 1) { return boxBlur(boxBlur(gray, w, h, r), w, h, r); }
        var sw = Math.ceil(w / step), sh = Math.ceil(h / step), small = new Float32Array(sw * sh), x, y;
        for (y = 0; y < sh; y++) { for (x = 0; x < sw; x++) { small[y * sw + x] = gray[Math.min(h - 1, y * step) * w + Math.min(w - 1, x * step)]; } }
        // lift dark text out of the estimate: take a local maximum before blurring
        var lifted = new Float32Array(small.length);
        for (y = 0; y < sh; y++) {
            for (x = 0; x < sw; x++) {
                var m = 0;
                for (var dy = -2; dy <= 2; dy++) {
                    for (var dx = -2; dx <= 2; dx++) {
                        var yy = Math.min(sh - 1, Math.max(0, y + dy)), xx = Math.min(sw - 1, Math.max(0, x + dx));
                        if (small[yy * sw + xx] > m) { m = small[yy * sw + xx]; }
                    }
                }
                lifted[y * sw + x] = m;
            }
        }
        var rs = Math.max(3, Math.round(r / step));
        var bg = boxBlur(boxBlur(lifted, sw, sh, rs), sw, sh, rs);
        var full = new Float32Array(w * h);
        for (y = 0; y < h; y++) {
            var fy = Math.min(sh - 1, y / step), y0 = fy | 0, y1 = Math.min(sh - 1, y0 + 1), ty = fy - y0;
            for (x = 0; x < w; x++) {
                var fx = Math.min(sw - 1, x / step), x0 = fx | 0, x1 = Math.min(sw - 1, x0 + 1), tx = fx - x0;
                full[y * w + x] = bg[y0 * sw + x0] * (1 - tx) * (1 - ty) + bg[y0 * sw + x1] * tx * (1 - ty)
                    + bg[y1 * sw + x0] * (1 - tx) * ty + bg[y1 * sw + x1] * tx * ty;
            }
        }
        return full;
    }

    function smoothstep(a, b, v) { var t = Math.max(0, Math.min(1, (v - a) / (b - a))); return t * t * (3 - 2 * t); }

    function filter(source, mode) {
        if (!mode || mode === "color") { return copyCanvas(source); }
        var w = source.width, h = source.height;
        var out = copyCanvas(source), ctx = out.getContext("2d", { willReadFrequently: true });
        var img = ctx.getImageData(0, 0, w, h), d = img.data, n = w * h, i, p;
        var gray = toGray(img);

        if (mode === "gray") {
            for (i = 0, p = 0; i < n; i++, p += 4) { d[p] = d[p + 1] = d[p + 2] = gray[i]; }
            ctx.putImageData(img, 0, 0);
            return out;
        }

        var bg = backgroundOf(gray, w, h);
        if (mode === "enhanced") {
            // colour kept; paper pulled to white, ink to full strength
            for (i = 0, p = 0; i < n; i++, p += 4) {
                var k = 255 / Math.max(40, bg[i]);
                for (var ch = 0; ch < 3; ch++) {
                    var v = d[p + ch] * k;
                    v = 255 * smoothstep(20, 245, v);
                    d[p + ch] = v;
                }
            }
        } else { // "bw"
            for (i = 0, p = 0; i < n; i++, p += 4) {
                var norm = gray[i] / Math.max(40, bg[i]) * 255;
                var val = 255 * smoothstep(120, 200, norm);
                d[p] = d[p + 1] = d[p + 2] = val;
            }
        }
        ctx.putImageData(img, 0, 0);
        return out;
    }

    function rotate(source, quarterTurns) {
        var t = ((quarterTurns % 4) + 4) % 4;
        if (t === 0) { return source; }
        var swap = t % 2 === 1;
        var c = makeCanvas(swap ? source.height : source.width, swap ? source.width : source.height);
        var ctx = c.getContext("2d");
        ctx.translate(c.width / 2, c.height / 2);
        ctx.rotate(t * Math.PI / 2);
        ctx.drawImage(source, -source.width / 2, -source.height / 2);
        return c;
    }

    // how much two quadrilaterals (corners clockwise, as detect returns them) cover each other: 0 .. 1
    function overlap(p, q) { return polygonIou(p, q); }

    root.MatPaperScan = { detect: detect, overlap: overlap, levels: LEVELS.length, warp: warp, filter: filter, rotate: rotate, makeCanvas: makeCanvas };
})(window);
