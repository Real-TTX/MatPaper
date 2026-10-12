// Synthetic "photo of a sheet of paper" generator with ground truth (browser, no dependencies).
//   SceneLib.render(category, seed, photos) -> Promise<{ canvas, gt: { quad:[[x,y]x4] (0..1, tl,tr,br,bl), poly, hasSheet }, meta }>
// Everything is derived from (category, seed), so a scene can always be re-made.
(function (root) {
    "use strict";

    var TAU = Math.PI * 2;

    // ------------------------------------------------------------------ random + noise

    function rng(seed) {
        var a = (seed >>> 0) || 1;
        return function () {
            a = (a + 0x6D2B79F5) >>> 0;
            var t = a;
            t = Math.imul(t ^ (t >>> 15), t | 1);
            t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
            return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
        };
    }

    function Rr(r) {
        return {
            r: r,
            u: function (a, b) { return a + (b - a) * r(); },
            i: function (a, b) { return a + Math.floor((b - a + 1) * r()); },
            p: function (arr) { return arr[Math.floor(r() * arr.length)]; },
            c: function (p) { return r() < p; },
            g: function () { var u = 1 - r(), v = r(); return Math.sqrt(-2 * Math.log(u)) * Math.cos(TAU * v); },
            w: function (pairs) { // weighted pick: [[value, weight], ...]
                var tot = 0, k; for (k = 0; k < pairs.length; k++) { tot += pairs[k][1]; }
                var x = r() * tot; for (k = 0; k < pairs.length; k++) { x -= pairs[k][1]; if (x <= 0) { return pairs[k][0]; } }
                return pairs[pairs.length - 1][0];
            }
        };
    }

    function Noise(r) {
        var perm = new Uint8Array(512), vals = new Float32Array(256), i, j, t;
        for (i = 0; i < 256; i++) { perm[i] = i; vals[i] = r(); }
        for (i = 255; i > 0; i--) { j = (r() * (i + 1)) | 0; t = perm[i]; perm[i] = perm[j]; perm[j] = t; }
        for (i = 0; i < 256; i++) { perm[256 + i] = perm[i]; }
        function lat(x, y) { return vals[perm[perm[x & 255] + (y & 255)]]; }
        function n2(x, y) {
            var xi = Math.floor(x), yi = Math.floor(y), xf = x - xi, yf = y - yi;
            var u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);
            var a = lat(xi, yi), b = lat(xi + 1, yi), c = lat(xi, yi + 1), d = lat(xi + 1, yi + 1);
            return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
        }
        function fbm(x, y, oct, gain) {
            var s = 0, amp = 1, tot = 0, f = 1;
            for (var o = 0; o < oct; o++) { s += amp * n2(x * f, y * f); tot += amp; amp *= gain || 0.5; f *= 2; }
            return s / tot;
        }
        return { n2: n2, fbm: fbm };
    }

    function mk(w, h) {
        var c = document.createElement("canvas");
        c.width = Math.max(1, Math.round(w)); c.height = Math.max(1, Math.round(h));
        return c;
    }
    function rgb(c, a) { return "rgba(" + (c[0] | 0) + "," + (c[1] | 0) + "," + (c[2] | 0) + "," + (a == null ? 1 : a) + ")"; }
    function clamp(v, a, b) { return Math.max(a, Math.min(b, v)); }
    function mix(a, b, t) { return [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t]; }
    function tri(r) { return r() + r() - 1; }

    // ------------------------------------------------------------------ backgrounds

    function bgCanvas(W, H, rr, noise, spec, photos) {
        var c = mk(W, H), ctx = c.getContext("2d"), t = spec.type, x, y, o;
        if (t === "photo") {
            var im = photos[spec.photo % photos.length];
            var z = spec.zoom || 1.3;
            var sw = Math.min(im.naturalWidth, im.naturalHeight * W / H) / z, sh = sw * H / W;
            var sx = (im.naturalWidth - sw) * rr.r(), sy = (im.naturalHeight - sh) * rr.r();
            ctx.drawImage(im, sx, sy, sw, sh, 0, 0, W, H);
            if (spec.dim) { ctx.fillStyle = "rgba(0,0,0," + spec.dim + ")"; ctx.fillRect(0, 0, W, H); }
            return c;
        }
        var img = ctx.createImageData(W, H), d = img.data;
        var base = spec.base || [128, 128, 128];
        if (t === "wood") {
            var ang = spec.angle, ca = Math.cos(ang), sa = Math.sin(ang);
            for (y = 0; y < H; y++) {
                for (x = 0; x < W; x++) {
                    var u = x * ca + y * sa, v = -x * sa + y * ca;
                    var pi = Math.floor(u / spec.plank), pu = u - pi * spec.plank;
                    var off = (noise.n2(pi * 7.3 + 0.5, 0.5) - 0.5) * spec.plankVar;
                    var ring = noise.fbm(u * spec.grain, v * spec.along + pi * 3.1, 4, 0.55);
                    var fine = noise.n2(u * 0.35, v * 0.03);
                    var val = 0.8 + off + spec.contrast * (ring - 0.5) + 0.12 * (fine - 0.5);
                    if (pu < 2.2) { val *= 0.55; }
                    o = (y * W + x) * 4;
                    d[o] = base[0] * val; d[o + 1] = base[1] * val; d[o + 2] = base[2] * val; d[o + 3] = 255;
                }
            }
        } else if (t === "desk") {
            var ox = rr.u(0, 50), oy = rr.u(0, 50);
            for (y = 0; y < H; y++) {
                for (x = 0; x < W; x++) {
                    var vv = 1 + spec.var * (noise.fbm(x / 600 + ox, y / 600 + oy, 2) - 0.5) * 2 + tri(rr.r) * spec.grain / 128;
                    o = (y * W + x) * 4;
                    d[o] = base[0] * vv; d[o + 1] = base[1] * vv; d[o + 2] = base[2] * vv; d[o + 3] = 255;
                }
            }
        } else if (t === "cloth") {
            var cb = spec.beta, cc = Math.cos(cb), sc = Math.sin(cb), c1 = spec.c1, c2 = spec.c2, ts = spec.tile;
            for (y = 0; y < H; y++) {
                for (x = 0; x < W; x++) {
                    var uu = x * cc + y * sc, vv2 = -x * sc + y * cc;
                    var k = (Math.floor(uu / ts) & 1) + (Math.floor(vv2 / ts) & 1);
                    var col = k === 0 ? c1 : (k === 1 ? mix(c1, c2, 0.45) : c2);
                    var weave = 0.93 + 0.07 * Math.sin(uu * 1.9) * Math.sin(vv2 * 1.9) + tri(rr.r) * 0.03;
                    o = (y * W + x) * 4;
                    d[o] = col[0] * weave; d[o + 1] = col[1] * weave; d[o + 2] = col[2] * weave; d[o + 3] = 255;
                }
            }
        } else if (t === "carpet") {
            for (y = 0; y < H; y++) {
                for (x = 0; x < W; x++) {
                    var cv = 1 + 0.30 * (noise.n2(x * 0.9, y * 0.9) - 0.5) + 0.12 * (noise.fbm(x / 90, y / 90, 3) - 0.5);
                    o = (y * W + x) * 4;
                    d[o] = base[0] * cv; d[o + 1] = base[1] * cv; d[o + 2] = base[2] * cv; d[o + 3] = 255;
                }
            }
        } else if (t === "marble") {
            for (y = 0; y < H; y++) {
                for (x = 0; x < W; x++) {
                    var w = noise.fbm(x / 180, y / 180, 4);
                    var vein = Math.abs(Math.sin((x * spec.vx + y * spec.vy + w * 5) * Math.PI));
                    var mv = 0.95 - spec.veinAmt * Math.pow(1 - vein, 12) + 0.05 * (noise.fbm(x / 22, y / 22, 2) - 0.5);
                    o = (y * W + x) * 4;
                    d[o] = base[0] * mv; d[o + 1] = base[1] * mv; d[o + 2] = base[2] * mv; d[o + 3] = 255;
                }
            }
        } else if (t === "granite") {
            for (y = 0; y < H; y++) {
                for (x = 0; x < W; x++) {
                    var n = noise.n2(x * 0.55, y * 0.55), gv = 1;
                    if (n > 0.7) { gv = 0.7; } else if (n < 0.25) { gv = 1.12; }
                    gv *= 1 + 0.08 * (noise.fbm(x / 120, y / 120, 2) - 0.5);
                    o = (y * W + x) * 4;
                    d[o] = base[0] * gv; d[o + 1] = base[1] * gv; d[o + 2] = base[2] * gv; d[o + 3] = 255;
                }
            }
        } else if (t === "keyboard") {
            var pitch = spec.pitch, kb = spec.beta, ckb = Math.cos(kb), skb = Math.sin(kb);
            for (y = 0; y < H; y++) {
                for (x = 0; x < W; x++) {
                    var ku = x * ckb + y * skb, kv = -x * skb + y * ckb;
                    var kx = ((ku % pitch) + pitch) % pitch, ky = ((kv % pitch) + pitch) % pitch;
                    var inside = kx > 4 && kx < pitch - 4 && ky > 4 && ky < pitch - 4;
                    var kval = inside ? spec.key * (0.9 + 0.2 * noise.n2(Math.floor(ku / pitch) * 3.7, Math.floor(kv / pitch) * 5.1)) : spec.gap;
                    if (inside && ky < 9) { kval *= 1.25; }
                    o = (y * W + x) * 4;
                    d[o] = kval; d[o + 1] = kval; d[o + 2] = kval * 1.04; d[o + 3] = 255;
                }
            }
        }
        ctx.putImageData(img, 0, 0);
        return c;
    }

    function backgroundSpec(rr, kind) {
        // kind: "wood-dark" "wood-light" "desk-dark" "desk-mid" "desk-light" "desk-white" "cloth" "carpet" "marble" "granite" "photo" "keyboard"
        switch (kind) {
            case "wood-dark": return { type: "wood", base: [rr.i(70, 120), rr.i(42, 80), rr.i(22, 50)], angle: rr.u(0, Math.PI), plank: rr.u(130, 280), plankVar: 0.25, grain: rr.u(0.006, 0.014), along: rr.u(0.0008, 0.002), contrast: rr.u(0.4, 0.8) };
            case "wood-light": return { type: "wood", base: [rr.i(190, 240), rr.i(140, 190), rr.i(90, 140)], angle: rr.u(0, Math.PI), plank: rr.u(130, 280), plankVar: 0.2, grain: rr.u(0.006, 0.014), along: rr.u(0.0008, 0.002), contrast: rr.u(0.3, 0.7) };
            case "desk-dark": { var l = rr.i(22, 70); return { type: "desk", base: [l, l, l + rr.i(0, 8)], var: 0.08, grain: 3 }; }
            case "desk-mid": { var m = rr.i(90, 150); return { type: "desk", base: [m + rr.i(-10, 10), m, m + rr.i(-10, 10)], var: 0.1, grain: 3 }; }
            case "desk-light": { var q = rr.i(175, 215); return { type: "desk", base: [q + rr.i(-6, 12), q + rr.i(-4, 8), q + rr.i(-12, 4)], var: 0.07, grain: 3 }; }
            case "desk-white": { var w = rr.i(215, 242); return { type: "desk", base: [w, w, w - rr.i(0, 10)], var: 0.05, grain: 2.5 }; }
            case "cloth": return { type: "cloth", beta: rr.u(-0.3, 0.3) + (rr.c(0.4) ? 0.785 : 0), tile: rr.u(24, 70), c1: rr.p([[238, 238, 236], [225, 225, 215], [245, 235, 225]]), c2: rr.p([[190, 40, 40], [40, 70, 160], [40, 130, 70], [30, 30, 30], [210, 160, 40], [150, 60, 130]]) };
            case "carpet": return { type: "carpet", base: rr.p([[110, 110, 110], [125, 98, 72], [88, 100, 122], [190, 176, 150], [70, 70, 75], [150, 120, 120]]) };
            case "marble": return { type: "marble", base: rr.p([[225, 225, 222], [208, 204, 196], [190, 195, 200]]), vx: rr.u(0.002, 0.006), vy: rr.u(0.002, 0.006), veinAmt: rr.u(0.15, 0.4) };
            case "granite": return { type: "granite", base: rr.p([[165, 162, 158], [120, 118, 118], [200, 195, 185], [90, 85, 85]]) };
            case "photo": return { type: "photo", photo: rr.i(0, 1000), zoom: rr.u(1.1, 2.6), dim: rr.c(0.4) ? rr.u(0.1, 0.4) : 0 };
            case "keyboard": return { type: "keyboard", pitch: rr.i(44, 70), beta: rr.u(-0.2, 0.2), key: rr.i(55, 120), gap: rr.i(12, 28) };
        }
        return { type: "desk", base: [128, 128, 128], var: 0.05, grain: 2 };
    }

    // ------------------------------------------------------------------ the sheet

    var INKS = [[25, 25, 28], [40, 40, 45], [55, 55, 60], [20, 30, 70], [70, 70, 70]];

    function textBlock(ctx, rr, x, y, w, lines, lh, ink, thick) {
        var line, wx, ww, hh = lh * (thick || 0.36);
        for (line = 0; line < lines; line++) {
            var yy = y + line * lh;
            if (rr.c(0.06)) { continue; }
            var last = line === lines - 1 || rr.c(0.06), limit = last ? x + w * rr.u(0.2, 0.8) : x + w;
            wx = x;
            while (wx < limit - 12) {
                ww = Math.min(limit - wx, rr.u(16, 90));
                ctx.fillStyle = rgb(ink, rr.u(0.65, 0.95));
                ctx.fillRect(wx, yy, ww, hh);
                // a few stems so the word is not a flat bar
                if (ww > 30 && rr.c(0.7)) { ctx.fillRect(wx + ww * rr.u(0.1, 0.9), yy - hh * 0.35, 2, hh * 1.7); }
                wx += ww + rr.u(7, 13);
            }
        }
    }

    function paperTexture(rr, pw, ph, spec) {
        var c = mk(pw, ph), ctx = c.getContext("2d");
        var tint = spec.tint, ink = rr.p(INKS), i, j, x, y;
        ctx.fillStyle = rgb(tint); ctx.fillRect(0, 0, pw, ph);
        var m = pw * rr.u(0.07, 0.12), st = spec.style, lh = pw * rr.u(0.032, 0.042);
        if (st === "letter" || st === "darkband") {
            if (st === "darkband") { ctx.fillStyle = "rgb(20,20,24)"; ctx.fillRect(0, 0, pw, ph * 0.14); ctx.fillStyle = "rgb(235,235,235)"; ctx.fillRect(m, ph * 0.05, pw * 0.4, ph * 0.035); }
            else {
                ctx.fillStyle = rgb(rr.p([[30, 70, 160], [180, 30, 30], [30, 30, 30], [20, 120, 80]])); ctx.fillRect(m, m * 0.7, pw * 0.22, pw * 0.08);
                textBlock(ctx, rr, pw - m - pw * 0.28, m * 0.8, pw * 0.28, 4, lh * 0.8, ink);
            }
            textBlock(ctx, rr, m, ph * 0.2, pw * 0.4, 5, lh, ink);
            var y = ph * 0.34;
            textBlock(ctx, rr, m, y, pw * 0.6, 1, lh * 1.4, ink, 0.55);
            y += lh * 2.2;
            for (i = 0; i < rr.i(3, 5); i++) { var n = rr.i(4, 8); textBlock(ctx, rr, m, y, pw - 2 * m, n, lh, ink); y += (n + 1.1) * lh; if (y > ph - m * 2) { break; } }
            textBlock(ctx, rr, m, ph - m * 1.1, pw * 0.5, 2, lh * 0.8, ink);
        } else if (st === "dense") {
            textBlock(ctx, rr, m, m, pw - 2 * m, Math.floor((ph - 2 * m) / lh), lh, ink);
        } else if (st === "sparse") {
            textBlock(ctx, rr, m, ph * 0.15, pw * 0.5, 3, lh, ink);
            textBlock(ctx, rr, m, ph * 0.5, pw - 2 * m, rr.i(1, 3), lh, ink);
        } else if (st === "invoice") {
            ctx.fillStyle = rgb(rr.p([[30, 70, 160], [30, 30, 30]])); ctx.fillRect(m, m * 0.8, pw * 0.3, pw * 0.07);
            textBlock(ctx, rr, m, ph * 0.16, pw * 0.4, 4, lh * 0.9, ink);
            var rows = rr.i(8, 16), cols = rr.i(3, 5), tx = m, ty = ph * 0.34, tw = pw - 2 * m, rh = (ph * 0.5) / rows;
            ctx.strokeStyle = "rgba(60,60,60,0.8)"; ctx.lineWidth = 2;
            ctx.strokeRect(tx, ty, tw, rh * rows);
            for (i = 1; i < rows; i++) { ctx.beginPath(); ctx.moveTo(tx, ty + i * rh); ctx.lineTo(tx + tw, ty + i * rh); ctx.stroke(); }
            for (j = 1; j < cols; j++) { ctx.beginPath(); ctx.moveTo(tx + j * tw / cols, ty); ctx.lineTo(tx + j * tw / cols, ty + rh * rows); ctx.stroke(); }
            for (i = 0; i < rows; i++) { for (j = 0; j < cols; j++) { textBlock(ctx, rr, tx + j * tw / cols + 8, ty + i * rh + rh * 0.35, tw / cols * rr.u(0.3, 0.7), 1, lh, ink); } }
        } else if (st === "form") {
            textBlock(ctx, rr, m, m, pw * 0.5, 2, lh * 1.3, ink, 0.5);
            var fy = ph * 0.14;
            for (i = 0; i < rr.i(8, 14); i++) {
                ctx.strokeStyle = "rgba(50,50,50,0.85)"; ctx.lineWidth = 2;
                ctx.strokeRect(m, fy, pw - 2 * m, lh * 1.7);
                textBlock(ctx, rr, m + 8, fy + lh * 0.5, pw * rr.u(0.2, 0.5), 1, lh, ink);
                fy += lh * 2.3; if (fy > ph - m * 2) { break; }
            }
        } else if (st === "notes" || st === "lined") {
            for (y = m * 1.4; y < ph - m * 0.6; y += lh * 1.1) { ctx.fillStyle = "rgba(90,130,200,0.55)"; ctx.fillRect(0, y, pw, 2); }
            ctx.fillStyle = "rgba(200,60,60,0.6)"; ctx.fillRect(m * 1.2, 0, 2, ph);
            ctx.strokeStyle = "rgba(30,30,90,0.8)"; ctx.lineWidth = 3;
            for (i = 0; i < rr.i(8, 18); i++) {
                var sy = m * 1.4 + i * lh * 1.1 - 4, sx = m * 1.6;
                ctx.beginPath(); ctx.moveTo(sx, sy);
                for (j = 0; j < rr.i(8, 22); j++) { sx += rr.u(6, 18); ctx.lineTo(sx, sy + rr.u(-9, 3)); if (sx > pw - m) { break; } }
                ctx.stroke();
            }
        } else if (st === "grid") {
            for (x = 0; x < pw; x += lh) { ctx.fillStyle = "rgba(110,150,210,0.45)"; ctx.fillRect(x, 0, 1.5, ph); }
            for (y = 0; y < ph; y += lh) { ctx.fillStyle = "rgba(110,150,210,0.45)"; ctx.fillRect(0, y, pw, 1.5); }
            textBlock(ctx, rr, m, m, pw * 0.6, 3, lh, ink);
        } else if (st === "photo") {
            textBlock(ctx, rr, m, m, pw * 0.7, 2, lh * 1.4, ink, 0.5);
            var g = ctx.createLinearGradient(0, ph * 0.2, pw, ph * 0.7);
            g.addColorStop(0, rgb(rr.p([[40, 60, 90], [90, 70, 50], [30, 80, 50]]))); g.addColorStop(1, rgb(rr.p([[180, 200, 220], [200, 170, 120], [120, 160, 120]])));
            ctx.fillStyle = g; ctx.fillRect(m, ph * 0.2, pw - 2 * m, ph * 0.45);
            textBlock(ctx, rr, m, ph * 0.7, pw - 2 * m, 5, lh, ink);
        } else if (st === "framed") {
            ctx.strokeStyle = "rgb(25,25,28)"; ctx.lineWidth = pw * 0.025;
            ctx.strokeRect(pw * 0.05, pw * 0.05, pw * 0.9, ph - pw * 0.1);
            textBlock(ctx, rr, m * 1.6, ph * 0.18, pw - 3.2 * m, 14, lh, ink);
        } else if (st === "receipt") {
            var ry = m * 0.8, k;
            for (k = 0; k < rr.i(18, 30); k++) {
                if (rr.c(0.15)) { ctx.fillStyle = "rgba(60,60,60,0.7)"; for (var dx = 0; dx < pw - 2 * m; dx += 14) { ctx.fillRect(m + dx, ry + 6, 8, 2); } ry += lh * 0.8; continue; }
                var half = rr.c(0.5);
                textBlock(ctx, rr, half ? m : m, ry, (pw - 2 * m) * (half ? 1 : rr.u(0.4, 0.9)), 1, lh * 0.8, ink, 0.45);
                ry += lh * 0.85;
                if (ry > ph - m * 3) { break; }
            }
            // bar code
            for (x = m; x < pw - m; x += rr.u(3, 8)) { ctx.fillStyle = "rgb(20,20,20)"; ctx.fillRect(x, ph - m * 2.4, rr.u(1.5, 4), m * 1.2); }
        }
        // stamp
        if (rr.c(0.25) && st !== "receipt") {
            ctx.strokeStyle = rgb(rr.p([[180, 40, 50], [40, 60, 160]]), 0.7); ctx.lineWidth = 4;
            ctx.beginPath(); ctx.arc(pw * rr.u(0.55, 0.8), ph * rr.u(0.6, 0.85), pw * 0.07, 0, TAU); ctx.stroke();
        }
        // paper grain
        var img = ctx.getImageData(0, 0, pw, ph), d = img.data, p;
        for (p = 0; p < d.length; p += 4) { var nz = tri(rr.r) * 2.2; d[p] += nz; d[p + 1] += nz; d[p + 2] += nz; }
        ctx.putImageData(img, 0, 0);
        return c;
    }

    // ------------------------------------------------------------------ geometry

    function orderQuad(q) {
        var cx = 0, cy = 0, i;
        for (i = 0; i < 4; i++) { cx += q[i][0] / 4; cy += q[i][1] / 4; }
        var s = q.slice().sort(function (a, b) { return Math.atan2(a[1] - cy, a[0] - cx) - Math.atan2(b[1] - cy, b[0] - cx); });
        var start = 0, best = Infinity;
        for (i = 0; i < 4; i++) { var v = s[i][0] + s[i][1]; if (v < best) { best = v; start = i; } }
        return [s[start], s[(start + 1) % 4], s[(start + 2) % 4], s[(start + 3) % 4]];
    }

    // projective map: dst = H(src), h[0..7]
    function solveH(src, dst) {
        var A = [], b = [], i, k, r;
        for (i = 0; i < 4; i++) {
            var x = src[i][0], y = src[i][1], u = dst[i][0], v = dst[i][1];
            A.push([x, y, 1, 0, 0, 0, -u * x, -u * y]); b.push(u);
            A.push([0, 0, 0, x, y, 1, -v * x, -v * y]); b.push(v);
        }
        for (var c = 0; c < 8; c++) {
            var piv = c;
            for (r = c + 1; r < 8; r++) { if (Math.abs(A[r][c]) > Math.abs(A[piv][c])) { piv = r; } }
            var tmp = A[c]; A[c] = A[piv]; A[piv] = tmp; var tb = b[c]; b[c] = b[piv]; b[piv] = tb;
            for (r = c + 1; r < 8; r++) { var f = A[r][c] / A[c][c]; for (k = c; k < 8; k++) { A[r][k] -= f * A[c][k]; } b[r] -= f * b[c]; }
        }
        var h = new Array(8);
        for (r = 7; r >= 0; r--) { var s = b[r]; for (k = r + 1; k < 8; k++) { s -= A[r][k] * h[k]; } h[r] = s / A[r][r]; }
        return h;
    }

    function pose(rr, W, H, ratio, p) {
        var a = 1 / Math.sqrt(ratio), b = Math.sqrt(ratio);
        var yaw = p.yaw * Math.PI / 180, pitch = p.pitch * Math.PI / 180, roll = p.roll * Math.PI / 180;
        var pts = [[-a / 2, -b / 2], [a / 2, -b / 2], [a / 2, b / 2], [-a / 2, b / 2]].map(function (q) {
            var x = q[0], y = q[1];
            var x1 = x * Math.cos(yaw), z1 = -x * Math.sin(yaw);
            var y2 = y * Math.cos(pitch) - z1 * Math.sin(pitch), z2 = y * Math.sin(pitch) + z1 * Math.cos(pitch);
            var Z = z2 + p.dist, px = p.f * x1 / Z, py = p.f * y2 / Z;
            return [px * Math.cos(roll) - py * Math.sin(roll), px * Math.sin(roll) + py * Math.cos(roll)];
        });
        var minx = Infinity, maxx = -Infinity, miny = Infinity, maxy = -Infinity;
        pts.forEach(function (q) { minx = Math.min(minx, q[0]); maxx = Math.max(maxx, q[0]); miny = Math.min(miny, q[1]); maxy = Math.max(maxy, q[1]); });
        var bw = maxx - minx, bh = maxy - miny, bx = (minx + maxx) / 2, by = (miny + maxy) / 2;
        var s = p.fill * Math.min(W / bw, H / bh);
        var cx = W / 2 + p.dx * W, cy = H / 2 + p.dy * H;
        var quad = pts.map(function (q) { return [(q[0] - bx) * s + cx, (q[1] - by) * s + cy]; });
        if (!p.cut) { // keep it inside the frame with a small margin: shift, then shrink
            for (var it = 0; it < 40; it++) {
                var mnx = Infinity, mxx = -Infinity, mny = Infinity, mxy = -Infinity;
                quad.forEach(function (q) { mnx = Math.min(mnx, q[0]); mxx = Math.max(mxx, q[0]); mny = Math.min(mny, q[1]); mxy = Math.max(mxy, q[1]); });
                var mgn = Math.max(4, 0.012 * Math.min(W, H));
                var sx = 0, sy = 0;
                if (mnx < mgn) { sx = mgn - mnx; } else if (mxx > W - mgn) { sx = W - mgn - mxx; }
                if (mny < mgn) { sy = mgn - mny; } else if (mxy > H - mgn) { sy = H - mgn - mxy; }
                if ((mxx - mnx) > W - 2 * mgn || (mxy - mny) > H - 2 * mgn) {
                    quad = quad.map(function (q) { return [(q[0] - cx) * 0.96 + cx, (q[1] - cy) * 0.96 + cy]; });
                } else if (sx || sy) { quad = quad.map(function (q) { return [q[0] + sx, q[1] + sy]; }); } else { break; }
            }
        }
        return quad;
    }

    function polyArea(p) { var a = 0; for (var i = 0; i < p.length; i++) { var j = (i + 1) % p.length; a += p[i][0] * p[j][1] - p[j][0] * p[i][1]; } return a / 2; }
    function clipPoly(subject, clip) { // Sutherland-Hodgman; clip must be convex
        var out = subject.slice(), sgn = polyArea(clip) >= 0 ? 1 : -1;
        for (var i = 0; i < clip.length && out.length; i++) {
            var a = clip[i], b = clip[(i + 1) % clip.length], input = out; out = [];
            var inside = function (p) { return sgn * ((b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0])) >= 0; };
            var inter = function (p, q) {
                var x1 = p[0], y1 = p[1], x2 = q[0], y2 = q[1], x3 = a[0], y3 = a[1], x4 = b[0], y4 = b[1];
                var den = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4); if (Math.abs(den) < 1e-12) { return q; }
                var t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / den;
                return [x1 + t * (x2 - x1), y1 + t * (y2 - y1)];
            };
            for (var j = 0; j < input.length; j++) {
                var cur = input[j], prev = input[(j + input.length - 1) % input.length];
                if (inside(cur)) { if (!inside(prev)) { out.push(inter(prev, cur)); } out.push(cur); } else if (inside(prev)) { out.push(inter(prev, cur)); }
            }
        }
        return out;
    }
    function iou(a, b) { // b convex
        var inter = clipPoly(a, b), ia = inter.length >= 3 ? Math.abs(polyArea(inter)) : 0;
        var ua = Math.abs(polyArea(a)) + Math.abs(polyArea(b)) - ia;
        return ua > 0 ? ia / ua : 0;
    }

    // ------------------------------------------------------------------ drawing the sheet into the scene

    function drawPaper(ctx, W, H, paper, quad, opt, rr, noise) {
        var pw = paper.width, ph = paper.height;
        var pd = paper.getContext("2d").getImageData(0, 0, pw, ph).data;
        var Hm = solveH(quad, [[0, 0], [pw, 0], [pw, ph], [0, ph]]);
        var minx = W, maxx = 0, miny = H, maxy = 0;
        quad.forEach(function (q) { minx = Math.min(minx, q[0]); maxx = Math.max(maxx, q[0]); miny = Math.min(miny, q[1]); maxy = Math.max(maxy, q[1]); });
        var curlPad = (opt.curl || 0) * Math.max(pw, ph) * 1.2;
        var x0 = clamp(Math.floor(minx - 3), 0, W - 1), x1 = clamp(Math.ceil(maxx + 3), 0, W - 1), y0 = clamp(Math.floor(miny - 3), 0, H - 1), y1 = clamp(Math.ceil(maxy + 3), 0, H - 1);
        var bw = x1 - x0 + 1, bh = y1 - y0 + 1;
        var img = ctx.getImageData(x0, y0, bw, bh), d = img.data;
        var sub = [[0.25, 0.25], [0.75, 0.25], [0.25, 0.75], [0.75, 0.75]];
        var ga = opt.shadeAngle, gc = Math.cos(ga), gs = Math.sin(ga), gamt = opt.shadeAmt, nox = rr.u(0, 99), noy = rr.u(0, 99);
        var curl = opt.curl || 0, curl2 = opt.curl2 || 0, edgeDark = opt.edgeDark || 0;
        for (var y = 0; y < bh; y++) {
            for (var x = 0; x < bw; x++) {
                var cr = 0, cg = 0, cb = 0, cnt = 0;
                for (var s = 0; s < 4; s++) {
                    var px = x0 + x + sub[s][0], py = y0 + y + sub[s][1];
                    var den = Hm[6] * px + Hm[7] * py + 1;
                    var u = (Hm[0] * px + Hm[1] * py + Hm[2]) / den, v = (Hm[3] * px + Hm[4] * py + Hm[5]) / den;
                    if (curl || curl2) { v -= curl * ph * Math.sin(Math.PI * u / pw); u -= curl2 * pw * Math.sin(Math.PI * v / ph); }
                    if (u < 0 || v < 0 || u >= pw - 1 || v >= ph - 1) { continue; }
                    var xi = u | 0, yi = v | 0, fx = u - xi, fy = v - yi, i00 = (yi * pw + xi) * 4, i10 = i00 + 4, i01 = i00 + pw * 4, i11 = i01 + 4;
                    var shade = 1 + gamt * ((u / pw - 0.5) * gc + (v / ph - 0.5) * gs) + 0.05 * (noise.fbm(u / 300 + nox, v / 300 + noy, 2) - 0.5);
                    if (edgeDark) { var ed = Math.min(u, pw - u, v, ph - v); if (ed < 10) { shade *= 1 - edgeDark * (1 - ed / 10); } }
                    for (var ch = 0; ch < 3; ch++) {
                        var val = pd[i00 + ch] * (1 - fx) * (1 - fy) + pd[i10 + ch] * fx * (1 - fy) + pd[i01 + ch] * (1 - fx) * fy + pd[i11 + ch] * fx * fy;
                        if (ch === 0) { cr += val * shade; } else if (ch === 1) { cg += val * shade; } else { cb += val * shade; }
                    }
                    cnt++;
                }
                if (!cnt) { continue; }
                var cov = cnt / 4, o = (y * bw + x) * 4;
                d[o] = d[o] * (1 - cov) + (cr / cnt) * cov; d[o + 1] = d[o + 1] * (1 - cov) + (cg / cnt) * cov; d[o + 2] = d[o + 2] * (1 - cov) + (cb / cnt) * cov;
            }
        }
        ctx.putImageData(img, x0, y0);
    }

    function polyPath(ctx, pts) { ctx.beginPath(); pts.forEach(function (p, i) { if (i) { ctx.lineTo(p[0], p[1]); } else { ctx.moveTo(p[0], p[1]); } }); ctx.closePath(); }

    function blurred(ctx, W, H, px, draw) { // draw something soft
        var t = mk(W, H), tc = t.getContext("2d");
        draw(tc); ctx.save(); ctx.filter = "blur(" + px + "px)"; ctx.drawImage(t, 0, 0); ctx.restore();
    }

    function roundRect(ctx, x, y, w, h, r) { ctx.beginPath(); ctx.moveTo(x + r, y); ctx.arcTo(x + w, y, x + w, y + h, r); ctx.arcTo(x + w, y + h, x, y + h, r); ctx.arcTo(x, y + h, x, y, r); ctx.arcTo(x, y, x + w, y, r); ctx.closePath(); }

    // ------------------------------------------------------------------ clutter

    function drawObject(ctx, W, H, rr, kind, at, scale) {
        var x = at[0], y = at[1], s = scale;
        ctx.save(); ctx.translate(x, y); ctx.rotate(rr.u(0, TAU));
        if (kind === "mug") {
            var r0 = s * rr.u(34, 60), col = rr.p([[235, 235, 235], [50, 90, 170], [190, 50, 50], [40, 40, 40], [230, 200, 80]]);
            ctx.fillStyle = "rgba(0,0,0,0.25)"; ctx.beginPath(); ctx.arc(8, 10, r0 * 1.04, 0, TAU); ctx.fill();
            var g = ctx.createRadialGradient(-r0 * 0.3, -r0 * 0.3, r0 * 0.1, 0, 0, r0); g.addColorStop(0, rgb(mix(col, [255, 255, 255], 0.35))); g.addColorStop(1, rgb(mix(col, [0, 0, 0], 0.25)));
            ctx.fillStyle = g; ctx.beginPath(); ctx.arc(0, 0, r0, 0, TAU); ctx.fill();
            ctx.fillStyle = rgb(mix(col, [0, 0, 0], 0.55)); ctx.beginPath(); ctx.arc(0, 0, r0 * 0.78, 0, TAU); ctx.fill();
        } else if (kind === "pen") {
            var len = s * rr.u(170, 300), wd = s * rr.u(8, 14), pc = rr.p([[30, 60, 160], [20, 20, 20], [200, 50, 40], [240, 200, 40], [40, 140, 80]]);
            ctx.fillStyle = "rgba(0,0,0,0.25)"; roundRect(ctx, -len / 2 + 5, -wd / 2 + 6, len, wd, wd / 2); ctx.fill();
            ctx.fillStyle = rgb(pc); roundRect(ctx, -len / 2, -wd / 2, len, wd, wd / 2); ctx.fill();
            ctx.fillStyle = "rgba(255,255,255,0.35)"; ctx.fillRect(-len / 2 + wd, -wd / 2 + 2, len - 2 * wd, 2);
        } else if (kind === "phone") {
            var pw2 = s * rr.u(70, 90), ph2 = pw2 * rr.u(1.9, 2.15), dark = rr.i(10, 35);
            ctx.fillStyle = "rgba(0,0,0,0.3)"; roundRect(ctx, -pw2 / 2 + 7, -ph2 / 2 + 9, pw2, ph2, 12); ctx.fill();
            ctx.fillStyle = rgb([dark, dark, dark + 4]); roundRect(ctx, -pw2 / 2, -ph2 / 2, pw2, ph2, 12); ctx.fill();
            if (rr.c(0.5)) { ctx.fillStyle = rgb(rr.p([[40, 70, 120], [30, 30, 40], [200, 200, 210]])); roundRect(ctx, -pw2 / 2 + 5, -ph2 / 2 + 8, pw2 - 10, ph2 - 16, 6); ctx.fill(); }
        } else if (kind === "coin") {
            var cr0 = s * rr.u(12, 20); ctx.fillStyle = "rgba(0,0,0,0.3)"; ctx.beginPath(); ctx.arc(3, 4, cr0, 0, TAU); ctx.fill();
            ctx.fillStyle = rgb(rr.p([[200, 170, 70], [190, 190, 195]])); ctx.beginPath(); ctx.arc(0, 0, cr0, 0, TAU); ctx.fill();
        } else if (kind === "sheet") { // a second, plainer sheet or envelope
            var sw2 = s * rr.u(260, 420), sh2 = sw2 * rr.u(0.7, 1.45), sc = rr.p([[245, 245, 243], [236, 232, 215], [250, 250, 250], [225, 230, 240]]);
            ctx.fillStyle = "rgba(0,0,0,0.22)"; ctx.fillRect(-sw2 / 2 + 5, -sh2 / 2 + 6, sw2, sh2);
            ctx.fillStyle = rgb(sc); ctx.fillRect(-sw2 / 2, -sh2 / 2, sw2, sh2);
            ctx.fillStyle = "rgba(60,60,60,0.5)"; for (var i = 0; i < 6; i++) { ctx.fillRect(-sw2 / 2 + 24, -sh2 / 2 + 30 + i * 22, sw2 * rr.u(0.3, 0.7), 8); }
        } else if (kind === "lightpatch") { // sun on the table: soft, bright, rectangular
            var lw = s * rr.u(300, 560), lh2 = lw * rr.u(0.5, 1.2);
            ctx.filter = "blur(" + Math.round(rr.u(8, 22)) + "px)"; ctx.fillStyle = "rgba(255,245,210," + rr.u(0.25, 0.5) + ")"; ctx.fillRect(-lw / 2, -lh2 / 2, lw, lh2); ctx.filter = "none";
        } else if (kind === "monitor") {
            var mw = s * rr.u(300, 520), mh = mw * rr.u(0.5, 0.65);
            ctx.fillStyle = "rgb(18,18,22)"; ctx.fillRect(-mw / 2, -mh / 2, mw, mh);
            ctx.fillStyle = rgb(rr.p([[50, 80, 140], [30, 40, 60], [200, 205, 215]])); ctx.fillRect(-mw / 2 + 8, -mh / 2 + 8, mw - 16, mh - 16);
        }
        ctx.restore();
    }

    function drawFinger(ctx, rr, from, dir, width, length) {
        // a thumb: rounded stroke from outside the page towards the edge
        var skin = rr.p([[226, 178, 146], [208, 152, 122], [240, 196, 168], [160, 110, 84], [120, 80, 60]]);
        ctx.save();
        ctx.lineCap = "round"; ctx.lineJoin = "round";
        ctx.strokeStyle = "rgba(0,0,0,0.22)"; ctx.lineWidth = width; ctx.beginPath(); ctx.moveTo(from[0] + 6, from[1] + 8); ctx.lineTo(from[0] + dir[0] * length + 6, from[1] + dir[1] * length + 8); ctx.stroke();
        ctx.strokeStyle = rgb(skin); ctx.lineWidth = width; ctx.beginPath(); ctx.moveTo(from[0], from[1]); ctx.lineTo(from[0] + dir[0] * length, from[1] + dir[1] * length); ctx.stroke();
        ctx.strokeStyle = rgb(mix(skin, [255, 255, 255], 0.3)); ctx.lineWidth = width * 0.28; ctx.beginPath(); ctx.moveTo(from[0] + dir[0] * length * 0.7, from[1] + dir[1] * length * 0.7); ctx.lineTo(from[0] + dir[0] * length * 0.97, from[1] + dir[1] * length * 0.97); ctx.stroke();
        ctx.restore();
    }

    // ------------------------------------------------------------------ camera

    function blurCanvas(src, s) {
        var c = mk(src.width, src.height), x = c.getContext("2d");
        x.drawImage(src, 0, 0); x.filter = "blur(" + s + "px)"; x.drawImage(src, 0, 0); x.filter = "none";
        return c;
    }
    function motionBlur(src, len, ang) {
        var c = mk(src.width, src.height), x = c.getContext("2d"), n = Math.max(2, Math.round(len));
        x.globalAlpha = 1;
        for (var k = 0; k < n; k++) { var t = k / (n - 1) - 0.5; x.globalAlpha = 1 / (k + 1); x.drawImage(src, Math.cos(ang) * len * t, Math.sin(ang) * len * t); }
        x.globalAlpha = 1;
        return c;
    }
    function cameraPixels(c, rr, cam) {
        var ctx = c.getContext("2d"), img = ctx.getImageData(0, 0, c.width, c.height), d = img.data, n = c.width * c.height, p, i;
        var tr = cam.tint[0], tg = cam.tint[1], tb = cam.tint[2], k = cam.contrast, b = cam.bright, sn = cam.noise, cn = cam.noise * 0.5;
        for (i = 0, p = 0; i < n; i++, p += 4) {
            var ln = sn ? tri(rr.r) * sn * 1.6 : 0;
            d[p] = ((d[p] * tr - 128) * k + 128 + b) + ln + (cn ? tri(rr.r) * cn : 0);
            d[p + 1] = ((d[p + 1] * tg - 128) * k + 128 + b) + ln + (cn ? tri(rr.r) * cn : 0);
            d[p + 2] = ((d[p + 2] * tb - 128) * k + 128 + b) + ln + (cn ? tri(rr.r) * cn : 0);
        }
        ctx.putImageData(img, 0, 0);
    }
    function jpegRound(c, q) {
        return new Promise(function (resolve) {
            c.toBlob(function (blob) {
                createImageBitmap(blob).then(function (bmp) { var o = mk(c.width, c.height); o.getContext("2d").drawImage(bmp, 0, 0); resolve(o); });
            }, "image/jpeg", q);
        });
    }

    // ------------------------------------------------------------------ scene specs

    var SIZES = { land: [1280, 720], four3: [1200, 900], port: [720, 1280], port43: [900, 1200] };
    var RATIOS = [[1.414, 5], [1.294, 2], [1.5, 0.6], [1.0, 0.25], [1.33, 0.4], [1.62, 0.3]];
    var STYLES = ["letter", "letter", "dense", "invoice", "form", "notes", "photo", "sparse", "darkband", "framed", "lined", "grid"];
    var TINTS = [[250, 250, 248], [244, 244, 240], [255, 253, 245], [238, 238, 236], [245, 238, 218], [236, 242, 250], [230, 230, 228]];

    function baseSpec(rr, cat) {
        var size = rr.w([["land", 4], ["four3", 4], ["port", 1.5], ["port43", 1]]);
        var s = {
            cat: cat, size: SIZES[size], sizeName: size, sheet: true,
            ratio: rr.w(RATIOS), style: rr.p(STYLES), tint: rr.p(TINTS),
            bgKind: rr.w([["wood-dark", 4], ["wood-light", 2], ["desk-dark", 3], ["desk-mid", 2]]),
            pose: { yaw: rr.u(-12, 12), pitch: rr.u(-8, 30), roll: rr.c(0.08) ? rr.p([90, -90, 180]) + rr.u(-6, 6) : rr.u(-25, 25), fill: rr.u(0.55, 0.9), dx: rr.u(-0.06, 0.06), dy: rr.u(-0.05, 0.05), dist: rr.u(3.2, 5), f: 3.2, cut: false },
            shade: { shadeAngle: rr.u(0, TAU), shadeAmt: rr.u(0, 0.18), edgeDark: rr.c(0.3) ? rr.u(0.03, 0.12) : 0, curl: 0, curl2: 0 },
            pageShadow: rr.c(0.8) ? { dx: rr.u(-10, 10), dy: rr.u(3, 14), a: rr.u(0.15, 0.5), blur: rr.u(2, 9) } : null,
            handShadow: null, glare: null, vignette: rr.c(0.5) ? rr.u(0.1, 0.4) : 0,
            fingers: 0, objects: [], secondSheets: 0, overlapObjects: false,
            cam: { blur: rr.c(0.6) ? rr.u(0.3, 1.0) : 0, motion: 0, motionAngle: 0, noise: rr.u(0, 2.5), jpeg: rr.u(0.75, 0.93), tint: [1 + rr.u(-0.05, 0.05), 1 + rr.u(-0.03, 0.03), 1 + rr.u(-0.06, 0.06)], contrast: rr.u(0.92, 1.1), bright: rr.u(-12, 8) }
        };
        return s;
    }

    var CATS = {
        easy: function (rr, s) { },
        lightdesk: function (rr, s) {
            s.bgKind = rr.w([["desk-white", 4], ["desk-light", 4], ["wood-light", 2], ["marble", 2]]);
            s.tint = rr.p([[252, 252, 250], [250, 250, 246], [246, 246, 244]]);
            s.pageShadow = { dx: rr.u(-8, 8), dy: rr.u(3, 12), a: rr.u(0.12, 0.4), blur: rr.u(3, 10) };
        },
        pattern: function (rr, s) {
            s.bgKind = rr.w([["cloth", 4], ["carpet", 2], ["marble", 2], ["granite", 3], ["photo", 5]]);
        },
        shadow: function (rr, s) {
            s.bgKind = rr.w([["wood-dark", 3], ["wood-light", 2], ["desk-mid", 2], ["desk-light", 2], ["desk-dark", 2]]);
            s.handShadow = { a: rr.u(0.2, 0.5), blur: rr.u(12, 40), n: rr.i(4, 6) };
            if (rr.c(0.6)) { s.glare = { a: rr.u(0.4, 0.9), r: rr.u(0.1, 0.28), x: rr.u(0.25, 0.75), y: rr.u(0.25, 0.75) }; }
            s.shade.shadeAmt = rr.u(0.1, 0.3);
        },
        occluded: function (rr, s) {
            s.fingers = rr.i(1, 3); s.overlapObjects = true; s.objects = rr.c(0.6) ? [rr.p(["pen", "pen", "coin"])] : [];
            s.pose.fill = rr.u(0.5, 0.82);
        },
        clutter: function (rr, s) {
            s.bgKind = rr.w([["wood-dark", 3], ["wood-light", 2], ["desk-dark", 2], ["keyboard", 3], ["desk-mid", 2]]);
            s.objects = []; for (var i = 0; i < rr.i(2, 5); i++) { s.objects.push(rr.p(["mug", "pen", "phone", "coin", "mug", "monitor"])); }
            s.secondSheets = rr.i(0, 2);
        },
        small: function (rr, s) {
            s.pose.fill = rr.u(0.32, 0.5); s.pose.dx = rr.u(-0.25, 0.25); s.pose.dy = rr.u(-0.2, 0.2);
            s.objects = rr.c(0.4) ? [rr.p(["mug", "pen", "phone"])] : [];
        },
        cut: function (rr, s) {
            s.pose.cut = true; s.pose.fill = rr.u(1.0, 1.25); s.pose.dx = rr.u(-0.08, 0.08); s.pose.dy = rr.u(-0.06, 0.06);
        },
        receipt: function (rr, s) {
            s.ratio = rr.u(2.4, 5); s.style = "receipt"; s.pose.fill = rr.u(0.6, 0.92); s.tint = rr.p([[250, 250, 248], [245, 243, 235]]);
            s.bgKind = rr.w([["wood-dark", 3], ["desk-dark", 2], ["desk-mid", 2], ["wood-light", 1], ["cloth", 1]]);
        },
        lowlight: function (rr, s) {
            s.cam.noise = rr.u(4, 9); s.cam.bright = rr.u(-60, -25); s.cam.contrast = rr.u(0.55, 0.8); s.cam.blur = rr.u(0.8, 1.6);
            s.bgKind = rr.w([["wood-dark", 3], ["desk-mid", 3], ["wood-light", 2], ["desk-dark", 2]]);
        },
        blurry: function (rr, s) {
            s.cam.motion = rr.u(6, 16); s.cam.motionAngle = rr.u(0, Math.PI); s.cam.blur = rr.u(0.5, 1.8); s.cam.noise = rr.u(1, 4);
        },
        curl: function (rr, s) {
            s.shade.curl = rr.u(0.006, 0.02) * (rr.c(0.5) ? 1 : -1); s.shade.curl2 = rr.c(0.5) ? rr.u(0.004, 0.012) * (rr.c(0.5) ? 1 : -1) : 0;
            s.pose.pitch = rr.u(10, 35); s.pose.yaw = rr.u(-18, 18);
        },
        tinted: function (rr, s) {
            s.tint = rr.p([[245, 235, 170], [236, 190, 200], [190, 215, 245], [200, 235, 200], [215, 215, 215], [230, 200, 160], [180, 180, 185]]);
            s.bgKind = rr.w([["wood-light", 2], ["desk-light", 2], ["desk-mid", 2], ["wood-dark", 2], ["desk-white", 1]]);
        },
        phoneport: function (rr, s) {
            s.size = SIZES.port; s.sizeName = "port"; s.pose.fill = rr.u(0.6, 0.92); s.pose.roll = rr.u(-12, 12) + (rr.c(0.3) ? 90 : 0);
        },
        highres: function (rr, s) {
            s.size = [2400, 1800]; s.sizeName = "hi"; s.cam.blur = rr.c(0.3) ? rr.u(0.5, 1.5) : 0; s.cam.noise = rr.u(0, 2);
            s.bgKind = rr.w([["wood-dark", 3], ["wood-light", 2], ["desk-dark", 2], ["desk-light", 2], ["cloth", 1], ["photo", 2]]);
        },
        nosheet: function (rr, s) {
            s.sheet = false;
            s.bgKind = rr.w([["wood-dark", 2], ["wood-light", 2], ["desk-dark", 1], ["desk-light", 2], ["desk-white", 2], ["cloth", 2], ["carpet", 1], ["marble", 1], ["granite", 1], ["photo", 4], ["keyboard", 3]]);
            s.objects = []; for (var i = 0; i < rr.i(0, 4); i++) { s.objects.push(rr.p(["mug", "pen", "phone", "coin", "lightpatch", "monitor"])); }
            if (rr.c(0.35)) { s.handShadow = { a: rr.u(0.2, 0.45), blur: rr.u(12, 40), n: rr.i(4, 6) }; }
            if (rr.c(0.25)) { s.glare = { a: rr.u(0.4, 0.8), r: rr.u(0.1, 0.25), x: rr.u(0.2, 0.8), y: rr.u(0.2, 0.8) }; }
        }
    };

    function render(cat, seed, photos) {
        var rr = Rr(rng(seed * 7919 + cat.length * 104729 + cat.charCodeAt(0) * 131)), noise = Noise(rng(seed * 31 + 7));
        var s = baseSpec(rr, cat);
        CATS[cat](rr, s);
        var W = s.size[0], H = s.size[1];
        var scale = W / 1280; // sizes of clutter / blur scale with the frame
        if (s.sizeName === "port" || s.sizeName === "port43") { scale = H / 1280; }
        if (s.sizeName === "hi") { scale = 1.9; }
        var bgKind = s.bgKind === "photo" && !(photos && photos.length) ? "desk-mid" : s.bgKind;   // no photographs given: a plain desk
        var bg = bgCanvas(W, H, rr, noise, backgroundSpec(rr, bgKind), photos);
        var c = mk(W, H), ctx = c.getContext("2d");
        ctx.drawImage(bg, 0, 0);

        var quad = null, ratio = s.ratio, paperFlat = null;
        if (s.sheet) { quad = pose(rr, W, H, ratio, s.pose); }
        var objectsAt = function () { return [rr.u(0.08, 0.92) * W, rr.u(0.1, 0.9) * H]; };

        // background clutter (not over the sheet unless asked)
        var placed = [];
        s.objects.forEach(function (kind) {
            var at, tries = 0, ok;
            do {
                at = objectsAt(); ok = true;
                if (quad && !s.overlapObjects) { var mnx = Infinity, mxx = -Infinity, mny = Infinity, mxy = -Infinity; quad.forEach(function (q) { mnx = Math.min(mnx, q[0]); mxx = Math.max(mxx, q[0]); mny = Math.min(mny, q[1]); mxy = Math.max(mxy, q[1]); }); var pad = 90 * scale; if (at[0] > mnx - pad && at[0] < mxx + pad && at[1] > mny - pad && at[1] < mxy + pad) { ok = false; } }
                tries++;
            } while (!ok && tries < 20);
            if (ok || s.overlapObjects) { placed.push({ kind: kind, at: at }); }
        });
        // objects under the sheet (drawn first) vs. on top: lightpatch/mug/monitor/phone first, pens/coins last when overlapping
        placed.forEach(function (o) { if (o.kind !== "pen" && o.kind !== "coin") { drawObject(ctx, W, H, rr, o.kind, o.at, scale); } });
        for (var k = 0; k < s.secondSheets; k++) { drawObject(ctx, W, H, rr, "sheet", objectsAt(), scale); }

        if (quad) {
            if (s.pageShadow) {
                var ps = s.pageShadow;
                blurred(ctx, W, H, ps.blur * scale, function (tc) { tc.fillStyle = "rgba(0,0,0," + ps.a + ")"; polyPath(tc, quad.map(function (q) { return [q[0] + ps.dx * scale, q[1] + ps.dy * scale]; })); tc.fill(); });
            }
            var appW = Math.max(Math.hypot(quad[1][0] - quad[0][0], quad[1][1] - quad[0][1]), Math.hypot(quad[2][0] - quad[3][0], quad[2][1] - quad[3][1]), Math.hypot(quad[3][0] - quad[0][0], quad[3][1] - quad[0][1]) / ratio, Math.hypot(quad[2][0] - quad[1][0], quad[2][1] - quad[1][1]) / ratio);
            var texW = clamp(Math.round(appW * 1.3), 400, 1600);
            var paper = paperTexture(rr, texW, Math.round(texW * ratio), { tint: s.tint, style: s.style });
            paperFlat = paper;
            drawPaper(ctx, W, H, paper, quad, s.shade, rr, noise);
        }
        placed.forEach(function (o) { if (o.kind === "pen" || o.kind === "coin") { drawObject(ctx, W, H, rr, o.kind, quad && rr.c(0.7) ? [quad[0][0] + (quad[2][0] - quad[0][0]) * rr.u(0.1, 0.9), quad[0][1] + (quad[2][1] - quad[0][1]) * rr.u(0.1, 0.9)] : o.at, scale); } });

        // fingers: from outside the page across an edge or corner
        for (var f = 0; f < s.fingers && quad; f++) {
            var side = rr.i(0, 3), A = quad[side], B = quad[(side + 1) % 4];
            var t = rr.c(0.5) ? rr.p([0.04, 0.96]) : rr.u(0.15, 0.85);
            var edge = [A[0] + (B[0] - A[0]) * t, A[1] + (B[1] - A[1]) * t];
            var cx = (quad[0][0] + quad[1][0] + quad[2][0] + quad[3][0]) / 4, cy = (quad[0][1] + quad[1][1] + quad[2][1] + quad[3][1]) / 4;
            var dir = [cx - edge[0], cy - edge[1]], dl = Math.hypot(dir[0], dir[1]); dir = [dir[0] / dl, dir[1] / dl];
            var reach = rr.u(0.04, 0.16) * Math.hypot(W, H) * 0.5, wd = rr.u(55, 95) * scale;
            var from = [edge[0] - dir[0] * (140 * scale) + dir[0] * reach, edge[1] - dir[1] * (140 * scale) + dir[1] * reach];
            drawFinger(ctx, rr, from, dir, wd, 140 * scale);
        }

        // soft shadow of a hand or phone across the scene
        if (s.handShadow) {
            var hs = s.handShadow, pts = [], ang0 = rr.u(0, TAU), rad = Math.min(W, H) * rr.u(0.22, 0.45), hx = W * rr.u(0.2, 0.8), hy = H * rr.u(0.2, 0.8);
            for (var v = 0; v < hs.n; v++) { var a = ang0 + v * TAU / hs.n + rr.u(-0.4, 0.4); pts.push([hx + Math.cos(a) * rad * rr.u(0.6, 1.4), hy + Math.sin(a) * rad * rr.u(0.6, 1.4)]); }
            blurred(ctx, W, H, hs.blur * scale, function (tc) { tc.fillStyle = "rgba(0,0,0," + hs.a + ")"; polyPath(tc, pts); tc.fill(); });
        }
        if (s.glare) {
            var gl = s.glare, gx = gl.x * W, gy = gl.y * H, gr = gl.r * Math.min(W, H);
            if (quad && rr.c(0.6)) { gx = (quad[0][0] + quad[2][0]) / 2 + rr.u(-0.25, 0.25) * W * 0.3; gy = (quad[0][1] + quad[2][1]) / 2 + rr.u(-0.25, 0.25) * H * 0.3; }
            var rg = ctx.createRadialGradient(gx, gy, 0, gx, gy, gr);
            rg.addColorStop(0, "rgba(255,255,255," + gl.a + ")"); rg.addColorStop(1, "rgba(255,255,255,0)");
            ctx.fillStyle = rg; ctx.fillRect(0, 0, W, H);
        }
        if (s.vignette) {
            var vg = ctx.createRadialGradient(W / 2, H / 2, Math.min(W, H) * 0.3, W / 2, H / 2, Math.hypot(W, H) * 0.55);
            vg.addColorStop(0, "rgba(0,0,0,0)"); vg.addColorStop(1, "rgba(0,0,0," + s.vignette + ")");
            ctx.fillStyle = vg; ctx.fillRect(0, 0, W, H);
        }

        // camera
        var out = c;
        if (s.cam.blur > 0.05) { out = blurCanvas(out, s.cam.blur * scale); }
        if (s.cam.motion > 0.5) { out = motionBlur(out, s.cam.motion * scale, s.cam.motionAngle); }
        cameraPixels(out, rr, s.cam);
        return jpegRound(out, s.cam.jpeg).then(function (final) {
            var gt = { hasSheet: s.sheet, quad: null, poly: null };
            if (quad) {
                var frame = [[0, 0], [W, 0], [W, H], [0, H]];
                var polyPx = s.pose.cut ? clipPoly(quad, frame) : quad;
                gt.quad = orderQuad(quad).map(function (q) { return [q[0] / W, q[1] / H]; });
                gt.poly = polyPx.map(function (q) { return [q[0] / W, q[1] / H]; });
                gt.visibleFrac = Math.abs(polyArea(polyPx)) / Math.abs(polyArea(quad));
            }
            return { canvas: final, paper: paperFlat, gt: gt, meta: { cat: cat, seed: seed, size: [W, H], bg: s.bgKind, style: s.style, ratio: +ratio.toFixed(2), fill: +(s.pose.fill).toFixed(2) } };
        });
    }

    root.SceneLib = { render: render, categories: Object.keys(CATS), iou: iou, orderQuad: orderQuad, polyArea: polyArea, clipPoly: clipPoly };
})(window);
