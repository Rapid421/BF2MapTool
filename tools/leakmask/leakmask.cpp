// leakmask: light-leak repair for a fluxbake output (Mustafar MUS8 defect 3: wall bases, crate bottoms and door-frame
// edges that reach below the single-sided floors see the lava from underneath; lightmap filtering spreads that light
// onto the visible faces).
//
//   leakmask <scene.fxs> <flux out dir> <new out dir> [--rays 48] [--range 0.6] [--frac 0.2] [--grow 2] [--pad 4] [--excess 1.3]
//
// 1. the scene's own geometry (the FXS1 file `frostycli enlighten flux-scene` wrote: meshes, instances, row transforms,
//    lightmap uv transforms) is rasterised into the atlas -> per texel: instance, world position, geometric normal,
//    world size of a texel;
// 2. a texel is HIDDEN when, from 2 cm off its surface, >= frac of `rays` hemisphere rays hit a BACK face first within
//    max(range, 2.5 texels): it sits behind / under a single-sided surface, where no camera can be;
// 3. hidden texels (grown by `grow` texels inside their instance: the baker's own filter has already smeared them that
//    far) take the value of the nearest visible texel of the same instance (breadth-first, 8-neighbour); the atlas
//    padding (`pad` texels around every chart) is then re-filled from the repaired charts;
// 4. every lightmap file is rewritten with the same texel-to-texel copy (irradiance.f32 / .rgba16f, dirR/G/B.bgra,
//    sky.bgra); probes.bin and stats.json are copied. mask.u8 (0 none, 1 visible, 2 hidden, 3 grown, 4 padding) and
//    leak_stats.json go to the new directory.
// Build: build.cmd (MSVC x64, /openmp). No dependency on Flux.
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <cstdint>
#include <string>
#include <vector>
#include <algorithm>
#include <map>
#include <array>
#include <direct.h>

struct V3 { float x, y, z; };
static inline V3 operator+(V3 a, V3 b) { return { a.x + b.x, a.y + b.y, a.z + b.z }; }
static inline V3 operator-(V3 a, V3 b) { return { a.x - b.x, a.y - b.y, a.z - b.z }; }
static inline V3 operator*(V3 a, float s) { return { a.x * s, a.y * s, a.z * s }; }
static inline float dot(V3 a, V3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
static inline V3 cross(V3 a, V3 b) { return { a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x }; }
static inline float len(V3 a) { return sqrtf(dot(a, a)); }
static inline V3 norm(V3 a) { float l = len(a); return l > 1e-20f ? a * (1.0f / l) : V3{ 0, 1, 0 }; }

static FILE* g_in;
static void rd(void* p, size_t n) { if (fread(p, 1, n, g_in) != n) { fprintf(stderr, "leakmask: scene truncated\n"); exit(2); } }
static uint32_t ru32() { uint32_t v; rd(&v, 4); return v; }
static float rf32() { float v; rd(&v, 4); return v; }
static void skip(size_t n) { if (_fseeki64(g_in, (long long)n, SEEK_CUR)) { fprintf(stderr, "leakmask: seek\n"); exit(2); } }

struct Tri { V3 a, b, c, n; };
struct Node { float lo[3], hi[3]; int left, right, first, count; };
static std::vector<Tri> T;
static std::vector<Node> N;
static std::vector<int> order;

static void bounds(int first, int count, float* lo, float* hi)
{
    for (int k = 0; k < 3; k++) { lo[k] = 1e30f; hi[k] = -1e30f; }
    for (int i = first; i < first + count; i++)
    {
        const Tri& t = T[order[i]];
        const V3* p[3] = { &t.a, &t.b, &t.c };
        for (int j = 0; j < 3; j++)
        {
            const float v[3] = { p[j]->x, p[j]->y, p[j]->z };
            for (int k = 0; k < 3; k++) { lo[k] = std::min(lo[k], v[k]); hi[k] = std::max(hi[k], v[k]); }
        }
    }
}
static int build(int first, int count)
{
    int id = (int)N.size(); N.push_back(Node());
    Node n; bounds(first, count, n.lo, n.hi); n.left = n.right = -1; n.first = first; n.count = count;
    if (count > 6)
    {
        int ax = 0; float e = -1;
        for (int k = 0; k < 3; k++) if (n.hi[k] - n.lo[k] > e) { e = n.hi[k] - n.lo[k]; ax = k; }
        auto cen = [ax](int i) { const Tri& t = T[i]; return ax == 0 ? t.a.x + t.b.x + t.c.x : ax == 1 ? t.a.y + t.b.y + t.c.y : t.a.z + t.b.z + t.c.z; };
        int mid = first + count / 2;
        std::nth_element(order.begin() + first, order.begin() + mid, order.begin() + first + count, [&](int x, int y) { return cen(x) < cen(y); });
        int l = build(first, mid - first), r = build(mid, first + count - mid);
        n.left = l; n.right = r; n.count = 0;
    }
    N[id] = n;
    return id;
}
// closest hit within tmax; returns triangle index or -1
static int trace(V3 o, V3 d, float tmax, float& tHit)
{
    float inv[3] = { 1.0f / (fabsf(d.x) > 1e-12f ? d.x : 1e-12f), 1.0f / (fabsf(d.y) > 1e-12f ? d.y : 1e-12f), 1.0f / (fabsf(d.z) > 1e-12f ? d.z : 1e-12f) };
    float oo[3] = { o.x, o.y, o.z };
    int stack[64], sp = 0, best = -1; stack[sp++] = 0; tHit = tmax;
    while (sp)
    {
        const Node& n = N[stack[--sp]];
        float t0 = 0, t1 = tHit;
        for (int k = 0; k < 3; k++)
        {
            float a = (n.lo[k] - oo[k]) * inv[k], b = (n.hi[k] - oo[k]) * inv[k];
            if (a > b) std::swap(a, b);
            t0 = std::max(t0, a); t1 = std::min(t1, b);
        }
        if (t0 > t1) continue;
        if (n.left < 0)
        {
            for (int i = n.first; i < n.first + n.count; i++)
            {
                const Tri& t = T[order[i]];
                V3 e1 = t.b - t.a, e2 = t.c - t.a, p = cross(d, e2);
                float det = dot(e1, p);
                if (fabsf(det) < 1e-12f) continue;
                float id = 1.0f / det; V3 s = o - t.a;
                float u = dot(s, p) * id; if (u < 0 || u > 1) continue;
                V3 q = cross(s, e1);
                float v = dot(d, q) * id; if (v < 0 || u + v > 1) continue;
                float tt = dot(e2, q) * id;
                if (tt <= 1e-4f || tt > tHit + 2e-3f) continue;
                // coincident twin faces (2005 double-sided pieces carry a flipped copy): the FRONT face wins a tie
                if (tt < tHit - 2e-3f || best < 0 || (dot(t.n, d) < 0 && dot(T[best].n, d) >= 0)) { tHit = std::min(tHit, tt); best = order[i]; }
            }
        }
        else if (sp < 62) { stack[sp++] = n.left; stack[sp++] = n.right; }
    }
    return best;
}

static uint16_t toHalf(float f)
{
    uint32_t x; memcpy(&x, &f, 4);
    uint32_t sign = (x >> 16) & 0x8000; int exp = (int)((x >> 23) & 0xFF) - 127 + 15; uint32_t mant = x & 0x7FFFFF;
    if (((x >> 23) & 0xFF) == 0xFF) return (uint16_t)(sign | 0x7C00 | (mant ? 0x200 : 0));
    if (exp >= 31) return (uint16_t)(sign | 0x7BFF);
    if (exp <= 0)
    {
        if (exp < -10) return (uint16_t)sign;
        mant |= 0x800000; int shift = 14 - exp;
        uint32_t h = mant >> shift; if ((mant >> (shift - 1)) & 1) h++;
        return (uint16_t)(sign | h);
    }
    uint32_t h = sign | ((uint32_t)exp << 10) | (mant >> 13);
    if (mant & 0x1000) h++;
    return (uint16_t)h;
}

template <class X> static bool load(const std::string& p, std::vector<X>& v, size_t n)
{
    FILE* f = fopen(p.c_str(), "rb"); if (!f) return false;
    v.resize(n); size_t r = fread(v.data(), sizeof(X), n, f); fclose(f); return r == n;
}
template <class X> static bool save(const std::string& p, const std::vector<X>& v)
{
    FILE* f = fopen(p.c_str(), "wb"); if (!f) { fprintf(stderr, "leakmask: cannot write %s\n", p.c_str()); return false; }
    size_t w = fwrite(v.data(), sizeof(X), v.size(), f); fclose(f); return w == v.size();
}
static void copyFile(const std::string& a, const std::string& b)
{
    FILE* f = fopen(a.c_str(), "rb"); if (!f) return; FILE* g = fopen(b.c_str(), "wb"); if (!g) { fclose(f); return; }
    char buf[65536]; size_t n; while ((n = fread(buf, 1, sizeof buf, f)) > 0) fwrite(buf, 1, n, g);
    fclose(f); fclose(g);
}

int main(int argc, char** argv)
{
    if (argc < 4) { fprintf(stderr, "usage: leakmask <scene.fxs> <flux out> <new out> [--rays n] [--range m] [--frac f] [--grow n] [--pad n]\n"); return 1; }
    std::string in = argv[2], out = argv[3];
    int RAYS = 48, GROW = 2, PAD = 4; float RANGE = 0.6f, FRAC = 0.2f, EXCESS = 1.3f;
    for (int i = 4; i + 1 < argc; i += 2)
    {
        if (!strcmp(argv[i], "--rays")) RAYS = atoi(argv[i + 1]); else if (!strcmp(argv[i], "--range")) RANGE = (float)atof(argv[i + 1]);
        else if (!strcmp(argv[i], "--frac")) FRAC = (float)atof(argv[i + 1]); else if (!strcmp(argv[i], "--grow")) GROW = atoi(argv[i + 1]);
        else if (!strcmp(argv[i], "--pad")) PAD = atoi(argv[i + 1]); else if (!strcmp(argv[i], "--excess")) EXCESS = (float)atof(argv[i + 1]);
    }
    g_in = fopen(argv[1], "rb"); if (!g_in) { fprintf(stderr, "leakmask: cannot open %s\n", argv[1]); return 1; }
    char magic[4]; rd(magic, 4); if (memcmp(magic, "FXS1", 4)) { fprintf(stderr, "leakmask: not FXS1\n"); return 1; }
    ru32(); uint32_t W = ru32(), H = ru32();
    ru32(); ru32(); rf32(); ru32(); ru32(); ru32(); ru32(); ru32(); float preExposure = rf32();
    uint32_t nTex = ru32();
    for (uint32_t t = 0; t < nTex; t++) { uint32_t tw = ru32(), th = ru32(), fmt = ru32(), lay = ru32(); skip((size_t)tw * th * (lay == 2 ? 6 : 1) * (fmt == 2 ? 16 : 4)); }
    uint32_t nL = ru32(); skip((size_t)nL * 164);
    uint32_t nM = ru32(); skip((size_t)nM * 64);
    struct Mesh { std::vector<float> v; std::vector<uint32_t> f; };
    std::map<std::array<uint8_t, 16>, int> meshIx; std::vector<Mesh> meshes;
    uint32_t nMesh = ru32(); meshes.resize(nMesh);
    for (uint32_t i = 0; i < nMesh; i++)
    {
        std::array<uint8_t, 16> g; rd(g.data(), 16); meshIx[g] = (int)i;
        uint32_t nv = ru32(); meshes[i].v.resize((size_t)nv * 10); rd(meshes[i].v.data(), (size_t)nv * 40);
        uint32_t nf = ru32(); meshes[i].f.resize((size_t)nf * 3);
        for (uint32_t f = 0; f < nf; f++) { rd(&meshes[i].f[f * 3], 12); skip(20); }
    }
    uint32_t nInst = ru32();
    struct Inst { int mesh; float t[12], uv[6]; int firstTri; };
    std::vector<Inst> insts(nInst);
    for (uint32_t i = 0; i < nInst; i++)
    {
        std::array<uint8_t, 16> g; skip(16); rd(g.data(), 16); ru32();
        auto it = meshIx.find(g); insts[i].mesh = it == meshIx.end() ? -1 : it->second;
        rd(insts[i].t, 48); rd(insts[i].uv, 24); ru32();
    }
    fclose(g_in);

    // world triangles (row transform, FluxScene.cs Xf)
    for (auto& x : insts)
    {
        x.firstTri = (int)T.size(); if (x.mesh < 0) continue;
        const Mesh& m = meshes[x.mesh]; const float* t = x.t;
        auto xf = [&](uint32_t vi) { const float* p = &m.v[(size_t)vi * 10];
            return V3{ p[0] * t[0] + p[1] * t[3] + p[2] * t[6] + t[9], p[0] * t[1] + p[1] * t[4] + p[2] * t[7] + t[10], p[0] * t[2] + p[1] * t[5] + p[2] * t[8] + t[11] }; };
        float det = t[0] * (t[4] * t[8] - t[5] * t[7]) - t[1] * (t[3] * t[8] - t[5] * t[6]) + t[2] * (t[3] * t[7] - t[4] * t[6]);
        for (size_t f = 0; f + 2 < m.f.size(); f += 3)
        {
            Tri tr; tr.a = xf(m.f[f]); tr.b = xf(m.f[f + 1]); tr.c = xf(m.f[f + 2]);
            V3 n = cross(tr.b - tr.a, tr.c - tr.a); if (det < 0) n = n * -1.0f;
            tr.n = norm(n); T.push_back(tr);
        }
    }
    order.resize(T.size()); for (size_t i = 0; i < T.size(); i++) order[i] = (int)i;
    N.reserve(T.size() * 2); build(0, (int)T.size());
    printf("leakmask: atlas %ux%u, %u meshes, %u instances, %zu world triangles, %zu bvh nodes\n", W, H, nMesh, nInst, T.size(), N.size());

    // rasterise the lightmapped instances
    size_t px = (size_t)W * H;
    std::vector<int32_t> texInst(px, -1); std::vector<float> pos(px * 3), nrm(px * 3), tsz(px, 0), edge(px, 1e9f);
    size_t lmInst = 0;
    for (size_t ii = 0; ii < insts.size(); ii++)
    {
        const Inst& x = insts[ii]; if (x.mesh < 0) continue;
        const float* u = x.uv; if (u[0] == 0 && u[1] == 0 && u[2] == 0 && u[3] == 0) continue;
        lmInst++;
        const Mesh& m = meshes[x.mesh];
        for (size_t f = 0; f + 2 < m.f.size(); f += 3)
        {
            const Tri& tr = T[x.firstTri + f / 3];
            float P[3][2];
            for (int k = 0; k < 3; k++)
            {
                const float* p = &m.v[(size_t)m.f[f + k] * 10];
                P[k][0] = (p[6] * u[0] + p[7] * u[2] + u[4]) * W; P[k][1] = (p[6] * u[1] + p[7] * u[3] + u[5]) * H;
            }
            float area2 = (P[1][0] - P[0][0]) * (P[2][1] - P[0][1]) - (P[2][0] - P[0][0]) * (P[1][1] - P[0][1]);
            if (fabsf(area2) < 1e-9f) continue;
            float wa = len(cross(tr.b - tr.a, tr.c - tr.a));
            float texel = sqrtf(wa / fabsf(area2));               // metres per texel
            int x0 = (int)floorf(std::min({ P[0][0], P[1][0], P[2][0] }) - 1), x1 = (int)ceilf(std::max({ P[0][0], P[1][0], P[2][0] }) + 1);
            int y0 = (int)floorf(std::min({ P[0][1], P[1][1], P[2][1] }) - 1), y1 = (int)ceilf(std::max({ P[0][1], P[1][1], P[2][1] }) + 1);
            x0 = std::max(x0, 0); y0 = std::max(y0, 0); x1 = std::min(x1, (int)W - 1); y1 = std::min(y1, (int)H - 1);
            for (int y = y0; y <= y1; y++) for (int xx = x0; xx <= x1; xx++)
            {
                float cx = xx + 0.5f, cy = y + 0.5f;
                float b1 = ((cx - P[0][0]) * (P[2][1] - P[0][1]) - (P[2][0] - P[0][0]) * (cy - P[0][1])) / area2;
                float b2 = ((P[1][0] - P[0][0]) * (cy - P[0][1]) - (cx - P[0][0]) * (P[1][1] - P[0][1])) / area2;
                float b0 = 1 - b1 - b2;
                float outside = std::max({ -b0, -b1, -b2, 0.0f });     // 0 when the texel centre is inside
                if (outside > 0)
                {   // within ~0.75 texel of the triangle: clamp onto it
                    float c0 = std::max(b0, 0.0f), c1 = std::max(b1, 0.0f), c2 = std::max(b2, 0.0f), s = c0 + c1 + c2;
                    c0 /= s; c1 /= s; c2 /= s;
                    float qx = c0 * P[0][0] + c1 * P[1][0] + c2 * P[2][0], qy = c0 * P[0][1] + c1 * P[1][1] + c2 * P[2][1];
                    float d = sqrtf((qx - cx) * (qx - cx) + (qy - cy) * (qy - cy));
                    if (d > 0.75f) continue;
                    outside = d + 1e-3f; b0 = c0; b1 = c1; b2 = c2;
                }
                size_t i = (size_t)y * W + xx;
                if (outside >= edge[i]) continue;
                edge[i] = outside; texInst[i] = (int32_t)ii; tsz[i] = texel;
                V3 p = tr.a * b0 + tr.b * b1 + tr.c * b2;
                pos[i * 3] = p.x; pos[i * 3 + 1] = p.y; pos[i * 3 + 2] = p.z; nrm[i * 3] = tr.n.x; nrm[i * 3 + 1] = tr.n.y; nrm[i * 3 + 2] = tr.n.z;
            }
        }
    }

    // hemisphere directions (cosine-weighted, Hammersley)
    std::vector<V3> dirs(RAYS);
    for (int i = 0; i < RAYS; i++)
    {
        uint32_t b = (uint32_t)i; b = (b << 16) | (b >> 16); b = ((b & 0x55555555u) << 1) | ((b & 0xAAAAAAAAu) >> 1); b = ((b & 0x33333333u) << 2) | ((b & 0xCCCCCCCCu) >> 2);
        b = ((b & 0x0F0F0F0Fu) << 4) | ((b & 0xF0F0F0F0u) >> 4); b = ((b & 0x00FF00FFu) << 8) | ((b & 0xFF00FF00u) >> 8);
        float u1 = (i + 0.5f) / RAYS, u2 = b * 2.3283064365386963e-10f, r = sqrtf(u1), ph = 6.2831853f * u2;
        dirs[i] = { r * cosf(ph), r * sinf(ph), sqrtf(std::max(0.0f, 1 - u1)) };
    }
    std::vector<uint8_t> mask(px, 0);
    long long nRaster = 0, nHidden = 0;
    #pragma omp parallel for schedule(dynamic, 16) reduction(+:nRaster, nHidden)
    for (long long y = 0; y < (long long)H; y++) for (uint32_t xx = 0; xx < W; xx++)
    {
        size_t i = (size_t)y * W + xx; if (texInst[i] < 0) continue;
        nRaster++;
        V3 n = { nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2] }, p = { pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2] };
        V3 t = norm(cross(fabsf(n.y) < 0.9f ? V3{ 0, 1, 0 } : V3{ 1, 0, 0 }, n)), b = cross(n, t);
        V3 o = p + n * 0.02f; float range = std::max(RANGE, 2.5f * tsz[i]); int back = 0;
        for (int k = 0; k < RAYS; k++)
        {
            V3 d = t * dirs[k].x + b * dirs[k].y + n * dirs[k].z; float th;
            int h = trace(o, d, range, th);
            if (h >= 0 && dot(T[h].n, d) > 0) back++;
        }
        if (back >= FRAC * RAYS) { mask[i] = 2; nHidden++; } else mask[i] = 1;
    }
    // grow the hidden region inside its instance (the baker's filter smeared it)
    long long nGrown = 0;
    for (int g = 0; g < GROW; g++)
    {
        std::vector<size_t> add;
        for (uint32_t y = 0; y < H; y++) for (uint32_t xx = 0; xx < W; xx++)
        {
            size_t i = (size_t)y * W + xx; if (mask[i] != 1) continue;
            bool hit = false;
            for (int dy = -1; dy <= 1 && !hit; dy++) for (int dx = -1; dx <= 1; dx++)
            {
                int X = (int)xx + dx, Y = (int)y + dy; if (X < 0 || Y < 0 || X >= (int)W || Y >= (int)H) continue;
                size_t j = (size_t)Y * W + X;
                if ((mask[j] == 2 || mask[j] == 3) && texInst[j] == texInst[i]) { hit = true; break; }
            }
            if (hit) add.push_back(i);
        }
        for (size_t i : add) mask[i] = 3;
        nGrown += (long long)add.size();
    }
    // nearest visible texel of the same instance (breadth-first)
    std::vector<uint32_t> src(px); for (size_t i = 0; i < px; i++) src[i] = (uint32_t)i;
    std::vector<uint8_t> done(px, 0); std::vector<size_t> q, nq;
    for (size_t i = 0; i < px; i++) if (mask[i] == 1) { done[i] = 1; q.push_back(i); }
    long long nFilled = 0;
    while (!q.empty())
    {
        nq.clear();
        for (size_t i : q)
        {
            int xx = (int)(i % W), y = (int)(i / W);
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
            {
                int X = xx + dx, Y = y + dy; if (X < 0 || Y < 0 || X >= (int)W || Y >= (int)H) continue;
                size_t j = (size_t)Y * W + X;
                if (done[j] || (mask[j] != 2 && mask[j] != 3) || texInst[j] != texInst[i]) continue;
                done[j] = 1; src[j] = src[i]; nq.push_back(j); nFilled++;
            }
        }
        q.swap(nq);
    }
    long long nStuck = 0; for (size_t i = 0; i < px; i++) if ((mask[i] == 2 || mask[i] == 3) && !done[i]) nStuck++;
    // atlas padding: texels outside every chart within `pad` of a chart take the nearest chart texel's (repaired) value
    q.clear(); for (size_t i = 0; i < px; i++) if (mask[i]) q.push_back(i);
    long long nPad = 0;
    for (int s = 0; s < PAD && !q.empty(); s++)
    {
        nq.clear();
        for (size_t i : q)
        {
            int xx = (int)(i % W), y = (int)(i / W);
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
            {
                int X = xx + dx, Y = y + dy; if (X < 0 || Y < 0 || X >= (int)W || Y >= (int)H) continue;
                size_t j = (size_t)Y * W + X; if (mask[j]) continue;
                mask[j] = 4; src[j] = src[i]; nq.push_back(j); nPad++;
            }
        }
        q.swap(nq);
    }
    pos.clear(); pos.shrink_to_fit(); nrm.clear(); nrm.shrink_to_fit(); edge.clear(); edge.shrink_to_fit();

    _mkdir(out.c_str());
    long long covFlux = 0, covBoth = 0, changed = 0; double before = 0, after = 0;
    {
        std::vector<float> I; if (!load(in + "/irradiance.f32", I, px * 4)) { fprintf(stderr, "leakmask: irradiance.f32\n"); return 3; }
        for (size_t i = 0; i < px; i++) { if (I[i * 4 + 3] > 0) { covFlux++; if (mask[i] >= 1 && mask[i] <= 3) covBoth++; } }
        std::vector<float> O(I);
        // a LEAK is excess light: only texels brighter than EXCESS x their visible source are rewritten, so the honest
        // darkening of creases under single-sided ceilings stays (first run: hidden texels are darker on average).
        auto lum = [](const float* c) { return 0.2126f * c[0] + 0.7152f * c[1] + 0.0722f * c[2]; };
        for (size_t i = 0; i < px; i++) if (src[i] != i)
        {
            const float* s = &I[(size_t)src[i] * 4];
            if (s[3] <= 0 || (mask[i] == 4 && I[i * 4 + 3] <= 0) || lum(&I[i * 4]) <= EXCESS * lum(s) + 0.05f) src[i] = (uint32_t)i;
        }
        for (size_t i = 0; i < px; i++) if (src[i] != i)
        {
            const float* s = &I[(size_t)src[i] * 4];
            before += I[i * 4] + I[i * 4 + 1] + I[i * 4 + 2]; after += s[0] + s[1] + s[2];
            memcpy(&O[i * 4], s, 16); changed++;
        }
        // remember which texels really change, for the 8-bit files
        save(out + "/irradiance.f32", O);
        std::vector<uint16_t> half(px * 4); for (size_t i = 0; i < px * 4; i++) half[i] = toHalf(O[i] * preExposure);
        save(out + "/irradiance.rgba16f", half);
    }
    const char* b8[4] = { "dirR.bgra", "dirG.bgra", "dirB.bgra", "sky.bgra" };
    for (int f = 0; f < 4; f++)
    {
        std::vector<uint32_t> D; if (!load(in + "/" + b8[f], D, px)) { fprintf(stderr, "leakmask: %s missing\n", b8[f]); continue; }
        std::vector<uint32_t> O(D); for (size_t i = 0; i < px; i++) if (src[i] != i) O[i] = D[src[i]];
        save(out + "/" + b8[f], O);
    }
    copyFile(in + "/probes.bin", out + "/probes.bin"); copyFile(in + "/stats.json", out + "/stats.json");
    save(out + "/mask.u8", mask);
    FILE* js = fopen((out + "/leak_stats.json").c_str(), "w");
    if (js)
    {
        fprintf(js, "{\"lightmappedInstances\": %zu, \"rasterTexels\": %lld, \"fluxCovered\": %lld, \"fluxCoveredAlsoRaster\": %lld, \"hidden\": %lld, \"grown\": %lld, "
                    "\"filled\": %lld, \"noVisibleTexelInInstance\": %lld, \"padding\": %lld, \"texelsRewritten\": %lld, \"rgbSumBefore\": %.1f, \"rgbSumAfter\": %.1f, "
                    "\"rays\": %d, \"range\": %g, \"frac\": %g, \"grow\": %d, \"pad\": %d}\n",
                lmInst, nRaster, covFlux, covBoth, nHidden, nGrown, nFilled, nStuck, nPad, changed, before, after, RAYS, RANGE, FRAC, GROW, PAD);
        fclose(js);
    }
    printf("leakmask: %zu lightmapped instances, raster %lld texels (flux covered %lld, %lld of them in the raster = %.1f %%)\n", lmInst, nRaster, covFlux, covBoth, covFlux ? 100.0 * covBoth / covFlux : 0);
    printf("leakmask: hidden %lld (%.2f %%), grown %lld, filled %lld, no visible texel in instance %lld, padding %lld; rewritten %lld, rgb sum %.0f -> %.0f\n",
           nHidden, nRaster ? 100.0 * nHidden / nRaster : 0, nGrown, nFilled, nStuck, nPad, changed, before, after);
    return 0;
}
