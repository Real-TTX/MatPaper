// Document scanner core: find the sheet in a photo, straighten it, clean it up.
// Pure image code on canvas pixels - no dependencies, runs in the browser (and is testable there).
//
//   MatPaperScan.detect(canvas)                      -> { quad:[tl,tr,br,bl] (0..1), score } | null
//   MatPaperScan.warp(canvas, quad, maxSide)         -> canvas (the sheet, straightened)
//   MatPaperScan.filter(canvas, "color|enhanced|gray|bw") -> canvas
//   MatPaperScan.rotate(canvas, quarterTurns)        -> canvas
//
// Detection: the sheet is usually the biggest bright area (Otsu split), else the area enclosed by
// strong edges. The outline of that area gets a convex hull, which is reduced to four corners.
(function (root) {
    "use strict";

    var ANALYSIS_WIDTH = 320;

    // ------------------------------------------------------------------ helpers

    function makeCanvas(w, h) {
        var c = document.createElement("canvas");
        c.width = Math.max(1, Math.round(w));
        c.height = Math.max(1, Math.round(h));
        return c;
    }

    function scaledImageData(source, width) {
        var sw = source.videoWidth || source.naturalWidth || source.width;
        var sh = source.videoHeight || source.naturalHeight || source.height;
        var scale = Math.min(1, width / sw);
        var c = makeCanvas(sw * scale, sh * scale);
        var ctx = c.getContext("2d", { willReadFrequently: true });
        ctx.drawImage(source, 0, 0, c.width, c.height);
        return ctx.getImageData(0, 0, c.width, c.height);
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

    function otsu(gray) {
        var hist = new Float64Array(256), i;
        for (i = 0; i < gray.length; i++) { hist[Math.max(0, Math.min(255, gray[i] | 0))]++; }
        var total = gray.length, sumAll = 0;
        for (i = 0; i < 256; i++) { sumAll += i * hist[i]; }
        var sumB = 0, wB = 0, best = 0, threshold = 127;
        for (i = 0; i < 256; i++) {
            wB += hist[i];
            if (wB === 0) { continue; }
            var wF = total - wB;
            if (wF === 0) { break; }
            sumB += i * hist[i];
            var mB = sumB / wB, mF = (sumAll - sumB) / wF;
            var between = wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; threshold = i; }
        }
        return threshold;
    }

    // The biggest 4-connected components of a binary mask (largest first). Each is { mask, area }.
    function components(mask, w, h, minArea, maxCount) {
        var labels = new Int32Array(mask.length), label = 0, found = [];
        var stack = new Int32Array(mask.length);
        for (var start = 0; start < mask.length; start++) {
            if (!mask[start] || labels[start]) { continue; }
            label++;
            var sp = 0, area = 0;
            stack[sp++] = start; labels[start] = label;
            while (sp > 0) {
                var p = stack[--sp]; area++;
                var x = p % w, y = (p - x) / w;
                if (x > 0 && mask[p - 1] && !labels[p - 1]) { labels[p - 1] = label; stack[sp++] = p - 1; }
                if (x < w - 1 && mask[p + 1] && !labels[p + 1]) { labels[p + 1] = label; stack[sp++] = p + 1; }
                if (y > 0 && mask[p - w] && !labels[p - w]) { labels[p - w] = label; stack[sp++] = p - w; }
                if (y < h - 1 && mask[p + w] && !labels[p + w]) { labels[p + w] = label; stack[sp++] = p + w; }
            }
            if (area >= minArea) { found.push({ label: label, area: area }); }
        }
        found.sort(function (u, v) { return v.area - u.area; });
        return found.slice(0, maxCount).map(function (c) {
            var out = new Uint8Array(mask.length);
            for (var i = 0; i < out.length; i++) { out[i] = labels[i] === c.label ? 1 : 0; }
            return { mask: out, area: c.area };
        });
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

    // A sheet is longer than wide by a paper-like ratio (A4 1.41, letter 1.29); photographed at an angle it varies.
    function aspectFit(q) {
        var w = (sideLength(q[0], q[1]) + sideLength(q[3], q[2])) / 2, h = (sideLength(q[0], q[3]) + sideLength(q[1], q[2])) / 2;
        var r = Math.max(w, h) / Math.max(1, Math.min(w, h));
        if (r >= 1.1 && r <= 1.8) { return 1; }
        if (r < 1.1) { return Math.max(0, 0.6 + (r - 1) * 4); }
        return Math.max(0, 1 - (r - 1.8) / 1.0);
    }

    // Printed text and lines give a sheet fine structure; a reflection or a bare table is smooth.
    function textureOf(mask, grad, w, h) {
        var sum = 0, n = 0;
        for (var y = 3; y < h - 3; y += 1) {
            for (var x = 3; x < w - 3; x += 1) {
                var i = y * w + x;
                // only well inside the region, away from its border
                if (mask[i] && mask[i - 3] && mask[i + 3] && mask[i - 3 * w] && mask[i + 3 * w]) { sum += grad[i]; n++; }
            }
        }
        return n > 20 ? sum / n : 0;
    }

    function gradientOf(gray, w, h) {
        var g = boxBlur(gray, w, h, 1), out = new Float32Array(gray.length);
        for (var y = 1; y < h - 1; y++) {
            for (var x = 1; x < w - 1; x++) {
                var i = y * w + x;
                var gx = g[i + 1] - g[i - 1], gy = g[i + w] - g[i - w];
                out[i] = Math.abs(gx) + Math.abs(gy);
            }
        }
        return out;
    }

    // Quadrilateral candidates from the biggest regions of a mask, each with quality measures.
    function candidatesFromMask(mask, w, h, grad, source) {
        var frame = w * h, result = [];
        components(mask, w, h, frame * 0.1, 3).forEach(function (comp) {
            if (comp.area > frame * 0.985) { return; }
            var boundary = [], sharpCount = 0, edgeCount = 0;
            for (var y = 0; y < h; y++) {
                for (var x = 0; x < w; x++) {
                    var i = y * w + x;
                    if (!comp.mask[i]) { continue; }
                    var onFrame = x === 0 || y === 0 || x === w - 1 || y === h - 1;
                    if (onFrame || !comp.mask[i - 1] || !comp.mask[i + 1] || !comp.mask[i - w] || !comp.mask[i + w]) {
                        boundary.push([x, y]);
                        // how sharp is the transition here? a sheet has a crisp edge, a reflection fades out
                        if (!onFrame) {
                            var m = 0;
                            for (var dy = -2; dy <= 2; dy++) { for (var dx = -2; dx <= 2; dx++) { var v = grad[(y + dy) * w + x + dx] || 0; if (v > m) { m = v; } } }
                            if (m > 18) { sharpCount++; }
                            edgeCount++;
                        }
                    }
                }
            }
            if (boundary.length < 8) { return; }
            var hull = convexHull(boundary);
            if (hull.length < 4) { return; }
            var quad = reduceToQuad(hull), quadArea = polygonArea(quad);
            if (quadArea <= 0) { return; }
            var ordered = orderQuad(quad);
            result.push({
                quad: ordered,
                fit: Math.min(comp.area, quadArea) / Math.max(comp.area, quadArea),
                areaFrac: quadArea / frame,
                rect: rectangularity(ordered),
                aspect: aspectFit(ordered),
                texture: textureOf(comp.mask, grad, w, h),
                // share of the outline that is a crisp edge (a sheet: nearly all; a reflection: little)
                sharp: edgeCount > 0 ? sharpCount / edgeCount : 0,
                source: source
            });
        });
        return result;
    }

    function edgeMask(gray, w, h, factor) {
        var blurred = boxBlur(gray, w, h, 1);
        var mag = new Float32Array(gray.length), sum = 0, sumSq = 0, n = 0, x, y;
        for (y = 1; y < h - 1; y++) {
            for (x = 1; x < w - 1; x++) {
                var i = y * w + x;
                var gx = -blurred[i - w - 1] - 2 * blurred[i - 1] - blurred[i + w - 1] + blurred[i - w + 1] + 2 * blurred[i + 1] + blurred[i + w + 1];
                var gy = -blurred[i - w - 1] - 2 * blurred[i - w] - blurred[i - w + 1] + blurred[i + w - 1] + 2 * blurred[i + w] + blurred[i + w + 1];
                var m = Math.sqrt(gx * gx + gy * gy);
                mag[i] = m; sum += m; sumSq += m * m; n++;
            }
        }
        var mean = sum / n, sd = Math.sqrt(Math.max(0, sumSq / n - mean * mean));
        var threshold = mean + sd * (factor || 0.8);
        var edges = new Uint8Array(gray.length);
        for (var k = 0; k < edges.length; k++) { edges[k] = mag[k] > threshold ? 1 : 0; }
        // close small gaps
        var closed = new Uint8Array(edges.length);
        for (y = 1; y < h - 1; y++) {
            for (x = 1; x < w - 1; x++) {
                var j = y * w + x;
                closed[j] = (edges[j] || edges[j - 1] || edges[j + 1] || edges[j - w] || edges[j + w]) ? 1 : 0;
            }
        }
        // what the border can reach without crossing an edge is background; the rest is the sheet
        var outside = new Uint8Array(edges.length), stack = new Int32Array(edges.length), sp = 0;
        function seed(p) { if (!closed[p] && !outside[p]) { outside[p] = 1; stack[sp++] = p; } }
        for (x = 0; x < w; x++) { seed(x); seed((h - 1) * w + x); }
        for (y = 0; y < h; y++) { seed(y * w); seed(y * w + w - 1); }
        while (sp > 0) {
            var p = stack[--sp], px = p % w, py = (p - px) / w;
            if (px > 0) { seed(p - 1); }
            if (px < w - 1) { seed(p + 1); }
            if (py > 0) { seed(p - w); }
            if (py < h - 1) { seed(p + w); }
        }
        var inside = new Uint8Array(edges.length);
        for (var m2 = 0; m2 < inside.length; m2++) { inside[m2] = outside[m2] ? 0 : 1; }
        return inside;
    }

    // ------------------------------------------------------------------ detect

    // Sensitivity 0 = strict (only a clear sheet), 1 = balanced, 2 = lenient (finds more, also false ones).
    var LEVELS = [
        { offset: 14, minScore: 0.80, minTexture: 4.5, minSharp: 0.30, minFit: 0.88, edge: 1.1 },
        { offset: 0, minScore: 0.68, minTexture: 2.2, minSharp: 0.20, minFit: 0.84, edge: 0.8 },
        { offset: -10, minScore: 0.52, minTexture: 0, minSharp: 0.08, minFit: 0.78, edge: 0.55 }
    ];

    function detect(source, options) {
        var level = LEVELS[Math.max(0, Math.min(2, (options && options.sensitivity) != null ? options.sensitivity : 1))];
        var img = scaledImageData(source, ANALYSIS_WIDTH);
        var w = img.width, h = img.height;
        var gray = toGray(img);
        var smooth = boxBlur(boxBlur(gray, w, h, 2), w, h, 2);
        var grad = gradientOf(gray, w, h);

        var candidates = [], i;
        var t = otsu(smooth);
        [level.offset, level.offset + 18].forEach(function (shift) {
            var bright = new Uint8Array(smooth.length);
            for (i = 0; i < bright.length; i++) { bright[i] = smooth[i] > t + shift ? 1 : 0; }
            candidates = candidates.concat(candidatesFromMask(bright, w, h, grad, "bright"));
        });
        candidates = candidates.concat(candidatesFromMask(edgeMask(smooth, w, h, level.edge), w, h, grad, "edges"));

        if (options && options.debug) { return candidates.map(function (c) { return { src: c.source, fit: +c.fit.toFixed(2), rect: +c.rect.toFixed(2), asp: +c.aspect.toFixed(2), tex: +c.texture.toFixed(1), sharp: +c.sharp.toFixed(2), area: +c.areaFrac.toFixed(2), q: c.quad.map(function (p) { return [Math.round(p[0] / w * 100) / 100, Math.round(p[1] / h * 100) / 100]; }) }; }); }
        var best = null;
        candidates.forEach(function (c) {
            if (c.fit < level.minFit || c.areaFrac < 0.1) { return; }
            var tex = c.texture >= level.minTexture ? 1 : (level.minTexture > 0 ? c.texture / level.minTexture : 1);
            var texScore = Math.min(1, c.texture / 8);
            // geometry first, then evidence that there is print on it, then size
            var edgeScore = Math.min(1, c.sharp / 0.9);
            var score = c.fit * 0.25 + c.rect * 0.30 + c.aspect * 0.10 + edgeScore * 0.10 + texScore * 0.08 + Math.min(1, c.areaFrac / 0.5) * 0.10;
            if (tex < 1) { score *= 0.55 + 0.45 * tex; }
            // a soft outline is a reflection or shadow, not a sheet
            if (c.sharp < level.minSharp) { return; }
            if (score < level.minScore) { return; }
            if (!best || score > best.score) { best = { quad: c.quad, score: score, source: c.source }; }
        });
        if (!best) { return null; }
        best.quad = best.quad.map(function (p) { return [p[0] / w, p[1] / h]; });
        return best;
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

    root.MatPaperScan = { detect: detect, levels: LEVELS.length, warp: warp, filter: filter, rotate: rotate, makeCanvas: makeCanvas };
})(window);
