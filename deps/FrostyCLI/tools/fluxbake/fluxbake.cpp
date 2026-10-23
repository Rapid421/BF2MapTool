// fluxbake: drive DICE's Flux lightmap / probe baker (Frostbite's Enlighten replacement, External/EA/Flux, API v64)
// from a plain binary scene written by `frostycli enlighten flux-scene`, and write the results in the encodings the
// Frostbite freeze pipeline stores (EnlightenFreezePipeline.cpp saveData):
//   irradiance.rgba16f   W x H x RGBA half: Flux irradiance x LightmapPreExposure
//   dirR/dirG/dirB.bgra  W x H x BGRA8: direction*0.5+0.5 (B=z, G=y, R=x), A = saturate(w) (rebalancing coefficient)
//   sky.bgra             W x H x BGRA8: .rgb = sky-visibility SH DC, .a = ambient occlusion
//   irradiance.f32       W x H x RGBA float, the raw Flux irradiance (for validation numbers)
//   probes.bin           per probe set: guid, count, then per probe 27 floats irradiance SH (r9 g9 b9),
//                        27 floats direct irradiance (0 if absent), 4 floats sky-visibility SHL1
//   stats.json
// Usage: fluxbake <scene.fxs> <outdir> [--samples n] [--depth n] [--aa n] [--threads n]
//
// Build: tools/fluxbake/build.cmd (MSVC x64, links Flux.lib from the Frostbite tree; Flux.dll is copied next to the exe).

#include <Flux/Flux.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <string>
#include <vector>
#include <chrono>
#include <direct.h>

using namespace Flux;

static FILE* g_in = nullptr;
static void rd(void* p, size_t n)
{
    if (fread(p, 1, n, g_in) != n) { fprintf(stderr, "fluxbake: scene file truncated\n"); exit(2); }
}
static uint32_t ru32() { uint32_t v; rd(&v, 4); return v; }
static float rf32() { float v; rd(&v, 4); return v; }
static void rfs(float* p, int n) { rd(p, 4 * (size_t)n); }
static Guid rguid() { Guid g; rd(&g, 16); return g; }

class Log : public ILogHandler
{
public:
    LogResult logInfo(const char* m, const char*, const char*, int) override { fprintf(stdout, "[flux] %s\n", m); fflush(stdout); return LogResult_Continue; }
    LogResult logWarning(const char* m, const char*, const char*, int) override { fprintf(stdout, "[flux WARN] %s\n", m); fflush(stdout); return LogResult_Continue; }
    LogResult logError(const char* m, const char*, const char*, int) override { fprintf(stdout, "[flux ERROR] %s\n", m); fflush(stdout); return LogResult_Continue; }
    LogResult logAssert(const char* e, const char* m, const char* f, const char* file, int line) override
    { fprintf(stdout, "[flux ASSERT] %s %s (%s %s:%d)\n", e ? e : "", m ? m : "", f ? f : "", file ? file : "", line); fflush(stdout); return LogResult_Continue; }
};

static uint16_t toHalf(float f)
{
    uint32_t x; memcpy(&x, &f, 4);
    uint32_t sign = (x >> 16) & 0x8000;
    int32_t exp = (int32_t)((x >> 23) & 0xFF) - 127 + 15;
    uint32_t mant = x & 0x7FFFFF;
    if (((x >> 23) & 0xFF) == 0xFF) return (uint16_t)(sign | 0x7C00 | (mant ? 0x200 : 0));
    if (exp >= 31) return (uint16_t)(sign | 0x7BFF);           // clamp to max finite (BC6H range)
    if (exp <= 0)
    {
        if (exp < -10) return (uint16_t)sign;
        mant |= 0x800000;
        uint32_t shift = (uint32_t)(14 - exp);
        uint32_t h = mant >> shift;
        if ((mant >> (shift - 1)) & 1) h++;
        return (uint16_t)(sign | h);
    }
    uint32_t h = sign | ((uint32_t)exp << 10) | (mant >> 13);
    if (mant & 0x1000) h++;                                      // round half up
    return (uint16_t)h;
}

static uint8_t u8sat(float v) { v = v < 0 ? 0 : v > 1 ? 1 : v; return (uint8_t)(v * 255.0f); }

static bool writeFile(const std::string& path, const void* data, size_t n)
{
    FILE* f = fopen(path.c_str(), "wb");
    if (!f) { fprintf(stderr, "fluxbake: cannot write %s\n", path.c_str()); return false; }
    size_t w = fwrite(data, 1, n, f);
    fclose(f);
    return w == n;
}

int main(int argc, char** argv)
{
    if (argc < 3) { fprintf(stderr, "usage: fluxbake <scene.fxs> <outdir> [--samples n] [--depth n] [--aa n]\n"); return 1; }
    std::string scenePath = argv[1], outDir = argv[2];
    int ovSamples = -1, ovDepth = -1, ovAa = -1, ovStitch = -1, ovFilter = -1, ovDirect = -1;
    for (int i = 3; i < argc; i++)
    {
        if (!strcmp(argv[i], "--samples") && i + 1 < argc) ovSamples = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--depth") && i + 1 < argc) ovDepth = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--aa") && i + 1 < argc) ovAa = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--stitch") && i + 1 < argc) ovStitch = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--filter") && i + 1 < argc) ovFilter = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--direct") && i + 1 < argc) ovDirect = atoi(argv[++i]);
    }
    _mkdir(outDir.c_str());

    if (getApiVersion() != ApiVersion) { fprintf(stderr, "fluxbake: Flux.dll API %u, header %u\n", getApiVersion(), (unsigned)ApiVersion); return 3; }
    static Log log;
    setLogHandler(&log);

    g_in = fopen(scenePath.c_str(), "rb");
    if (!g_in) { fprintf(stderr, "fluxbake: cannot open %s\n", scenePath.c_str()); return 1; }
    char magic[4]; rd(magic, 4);
    if (memcmp(magic, "FXS1", 4)) { fprintf(stderr, "fluxbake: not an FXS1 scene\n"); return 1; }
    uint32_t version = ru32();
    if (version != 1) { fprintf(stderr, "fluxbake: scene version %u unsupported\n", version); return 1; }
    uint32_t W = ru32(), H = ru32();
    uint32_t samples = ru32(), depth = ru32();
    float aoRadius = rf32();
    uint32_t components = ru32(), aa = ru32(), probeShl2 = ru32(), filterSize = ru32(), stitch = ru32();
    float preExposure = rf32();
    if (ovSamples > 0) samples = (uint32_t)ovSamples;
    if (ovDepth > 0) depth = (uint32_t)ovDepth;
    if (ovAa > 0) aa = (uint32_t)ovAa;
    if (ovStitch >= 0) stitch = (uint32_t)ovStitch;
    if (ovFilter >= 0) filterSize = (uint32_t)ovFilter;
    if (ovDirect == 1) components |= ComponentMask_DirectLighting; else if (ovDirect == 0) components &= ~(uint32_t)ComponentMask_DirectLighting;

    ISceneContext* scene = ISceneContext::create();
    LightmapAtlas* atlas = scene->addLightmapAtlases(1);
    atlas->id = 0; atlas->width = W; atlas->height = H;

    // textures first: the environment cube is texture 0 (a local light's textureId 0 means "none")
    uint32_t nTex = ru32();
    std::vector<uint32_t> texIds(nTex);   // scene file texture index -> Flux texture id (Flux keeps a dummy texture at id 0)
    for (uint32_t t = 0; t < nTex; t++)
    {
        uint32_t tw = ru32(), th = ru32(), fmt = ru32(), layout = ru32();
        TextureBuffer tb = scene->addTexture(tw, th, (TextureFormat)fmt, (TextureLayout)layout);
        texIds[t] = tb.id;
        size_t texels = (size_t)tw * th * (layout == TextureLayout_Cube ? 6 : 1);
        size_t bytes = texels * (fmt == TextureFormat_RGBA_Float ? 16 : 4);
        rd(tb.data, bytes);
    }

    uint32_t nLights = ru32();
    LightSource* lights = nLights ? scene->addLights(nLights) : nullptr;
    for (uint32_t i = 0; i < nLights; i++)
    {
        LightSource& l = lights[i];
        memset(&l, 0, sizeof(l));
        l.id = rguid();
        rfs(l.position, 3); rfs(l.forward, 3); rfs(l.left, 3); rfs(l.up, 3);
        rfs(l.color, 3); rfs(l.bounceColorScale, 3);
        l.lightType = (LightSourceType)ru32();
        uint32_t flags = ru32();
        l.directEnable = (flags & 1) != 0; l.indirectEnable = (flags & 2) != 0; l.shadowsEnable = true;
        l.attenuationRadius = rf32(); l.attenuationOffset = rf32();
        l.sphereRadius = rf32();
        l.coneInnerAngle = rf32(); l.coneOuterAngle = rf32();
        l.diskRadius = rf32();
        l.tubeRadius = rf32(); l.tubeWidth = rf32(); l.isCapsule = ru32() != 0;
        l.rectWidth = rf32(); l.rectHeight = rf32(); l.rectShape = (RectangularLightShape)ru32();
        l.frustumNear = rf32(); l.frustumFar = rf32(); l.frustumOuterAngle = rf32(); l.frustumAspect = rf32();
        l.textureId = ru32();
        // scene-file texture references: the environment names its cube by file index; a local light uses index + 1
        // (0 = no texture, as in the freeze pipeline). Both become the id Flux returned for that texture.
        if (l.lightType == LightSourceType_Environment) l.textureId = l.textureId < nTex ? texIds[l.textureId] : InvalidTextureId;
        else if (l.textureId != 0) l.textureId = l.textureId - 1 < nTex ? texIds[l.textureId - 1] : 0;
    }

    uint32_t nMat = ru32();
    LightmapMaterial* mats = nMat ? scene->addMaterials(nMat) : nullptr;
    for (uint32_t i = 0; i < nMat; i++)
    {
        LightmapMaterial& m = mats[i];
        memset(&m, 0, sizeof(m));
        m.id = rguid();
        m.variationNameHash = ru32();
        rfs(m.albedo, 3); rfs(m.emissive, 3);
        m.opacity = rf32(); m.reflectivity = rf32();
        m.albedoTextureId = ru32(); m.emissiveTextureId = ru32();
        m.backfaceType = (MaterialBackfaceType)ru32();
    }

    uint32_t nMesh = ru32();
    LightmapMesh* meshes = nMesh ? scene->addMeshes(nMesh) : nullptr;
    size_t totalTris = 0;
    for (uint32_t i = 0; i < nMesh; i++)
    {
        LightmapMesh& m = meshes[i];
        m.id = rguid();
        uint32_t nv = ru32();
        LightmapVertexBuffer vb = scene->addVertices(nv);
        for (uint32_t v = 0; v < nv; v++)
        {
            LightmapVertex& x = vb.data[v];
            rfs(x.position, 3); rfs(x.normal, 3); rfs(x.texcoordLightmap, 2); rfs(x.texcoordAlbedo, 2);
        }
        uint32_t nf = ru32();
        LightmapFaceBuffer fb = scene->addFaces(nf);
        for (uint32_t f = 0; f < nf; f++)
        {
            LightmapFace& face = fb.data[f];
            face.indices[0] = ru32(); face.indices[1] = ru32(); face.indices[2] = ru32();
            uint32_t chart = ru32();
            if (chart != 0xFFFFFFFFu) face.chartId = chart;   // else keep Flux's InvalidChartId (charts generated)
            face.materialId = rguid();
        }
        totalTris += nf;
        m.vertexBufferId = vb.id; m.vertexBufferOffset = 0; m.vertexCount = nv;
        m.faceBufferId = fb.id; m.faceBufferOffset = 0; m.faceCount = nf;
    }

    uint32_t nInst = ru32();
    LightmapMeshInstance* insts = nInst ? scene->addMesheInstances(nInst) : nullptr;
    for (uint32_t i = 0; i < nInst; i++)
    {
        LightmapMeshInstance& x = insts[i];
        memset(&x, 0, sizeof(x));
        x.id = rguid(); x.meshId = rguid();
        x.lightmapAtlasId = 0;
        x.variationNameHash = ru32();
        rfs(x.transform, 12); rfs(x.uvTransform, 6);
        x.receiveOnly = ru32() != 0;
    }

    uint32_t nSets = ru32();
    std::vector<Guid> setGuids(nSets);
    std::vector<uint32_t> setCounts(nSets);
    for (uint32_t s = 0; s < nSets; s++)
    {
        setGuids[s] = rguid();
        uint32_t n = ru32();
        setCounts[s] = n;
        PointBuffer pb = scene->addLightProbes(setGuids[s], n);
        for (uint32_t p = 0; p < n; p++) { pb.data[p].x = rf32(); pb.data[p].y = rf32(); pb.data[p].z = rf32(); }
    }
    fclose(g_in);
    printf("fluxbake: atlas %ux%u, %u textures, %u lights, %u materials, %u meshes (%zu tris), %u instances, %u probe sets\n",
           W, H, nTex, nLights, nMat, nMesh, totalTris, nInst, nSets);
    printf("fluxbake: samples %u, depth %u, aa %u, ao radius %.2f, components 0x%x, preExposure %g\n", samples, depth, aa, aoRadius, components, preExposure);
    fflush(stdout);

    IBakingContext* baker = IBakingContext::create(BakingContextFlag_None, 0);
    baker->setEnabledComponents(components);
    baker->setMaxHemisphereSamples(samples);
    baker->setMaxDepth(depth);
    baker->setAntialiasingQuality(aa);
    baker->setAmbientOcclusionRadius(aoRadius);
    baker->setIndirectLightmapFilterSize(filterSize);
    baker->setUseLightmapStitching(stitch != 0);
    baker->setDirectIndirectIrradianceSplit(true);
    baker->setLightmapFormat(LightmapFormat_DirectionalIrradiance);
    baker->setProbeDescriptor(probeShl2 ? ProbeDescriptor(ProbeRepresentation_SHL2) : ProbeDescriptor(ProbeRepresentation_SHL1));

    auto t0 = std::chrono::steady_clock::now();
    ReturnCode rc = baker->render(scene);
    double secs = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
    printf("fluxbake: render rc=%u in %.1f s\n", (unsigned)rc, secs);
    fflush(stdout);
    if (rc != ReturnCode_Success) return 4;

    TextureBuffer irr = baker->getIrradiance(0);
    TextureBuffer sky = baker->getSkyVisibility(0);
    TextureBuffer ao = baker->getAmbientOcclusion(0);
    TextureBuffer dr = baker->getLightDirection(0, ColorComponent_R);
    TextureBuffer dg = baker->getLightDirection(0, ColorComponent_G);
    TextureBuffer db = baker->getLightDirection(0, ColorComponent_B);
    if (!irr.data || !dr.data || !dg.data || !db.data) { fprintf(stderr, "fluxbake: missing lightmap outputs\n"); return 5; }
    uint32_t w = irr.width, h = irr.height;
    size_t px = (size_t)w * h;
    const float* I = (const float*)irr.data;
    writeFile(outDir + "/irradiance.f32", I, px * 16);
    std::vector<uint16_t> half(px * 4);
    for (size_t i = 0; i < px * 4; i++) half[i] = toHalf(I[i] * preExposure);
    writeFile(outDir + "/irradiance.rgba16f", half.data(), half.size() * 2);
    const float* D[3] = { (const float*)dr.data, (const float*)dg.data, (const float*)db.data };
    const char* dn[3] = { "dirR.bgra", "dirG.bgra", "dirB.bgra" };
    std::vector<uint8_t> b8(px * 4);
    for (int c = 0; c < 3; c++)
    {
        for (size_t i = 0; i < px; i++)
        {
            b8[i * 4 + 0] = u8sat(D[c][i * 4 + 2] * 0.5f + 0.5f);
            b8[i * 4 + 1] = u8sat(D[c][i * 4 + 1] * 0.5f + 0.5f);
            b8[i * 4 + 2] = u8sat(D[c][i * 4 + 0] * 0.5f + 0.5f);
            b8[i * 4 + 3] = u8sat(D[c][i * 4 + 3]);
        }
        writeFile(outDir + "/" + dn[c], b8.data(), b8.size());
    }
    if (sky.data && ao.data && sky.width == w && sky.height == h)
    {
        const float* S = (const float*)sky.data; const float* A = (const float*)ao.data;
        for (size_t i = 0; i < px; i++)
        {
            uint8_t s = u8sat(S[i * 4]);
            b8[i * 4 + 0] = s; b8[i * 4 + 1] = s; b8[i * 4 + 2] = s; b8[i * 4 + 3] = u8sat(A[i * 4]);
        }
        writeFile(outDir + "/sky.bgra", b8.data(), b8.size());
    }

    FILE* pf = fopen((outDir + "/probes.bin").c_str(), "wb");
    uint32_t probesOut = 0;
    if (pf)
    {
        fwrite(&nSets, 4, 1, pf);
        for (uint32_t s = 0; s < nSets; s++)
        {
            LightProbeBuffer pb = baker->getLightProbes(setGuids[s]);
            fwrite(&setGuids[s], 16, 1, pf);
            uint32_t n = pb.irradiance ? pb.count : 0;
            fwrite(&n, 4, 1, pf);
            for (uint32_t p = 0; p < n; p++)
            {
                const LightProbe& a = pb.irradiance[p];
                fwrite(a.r, 4, 9, pf); fwrite(a.g, 4, 9, pf); fwrite(a.b, 4, 9, pf);
                LightProbe z; memset(&z, 0, sizeof(z));
                const LightProbe& d = pb.directIrradiance ? pb.directIrradiance[p] : z;
                fwrite(d.r, 4, 9, pf); fwrite(d.g, 4, 9, pf); fwrite(d.b, 4, 9, pf);
                ProbeSHL1 zs; memset(&zs, 0, sizeof(zs));
                const ProbeSHL1& sv = pb.skyVisibility ? pb.skyVisibility[p] : zs;
                fwrite(sv.data, 4, 4, pf);
            }
            probesOut += n;
        }
        fclose(pf);
    }

    // texel statistics (covered = non-zero irradiance alpha)
    size_t covered = 0; double sumL = 0;
    for (size_t i = 0; i < px; i++)
        if (I[i * 4 + 3] > 0) { covered++; sumL += 0.2126 * I[i * 4] + 0.7152 * I[i * 4 + 1] + 0.0722 * I[i * 4 + 2]; }
    FILE* js = fopen((outDir + "/stats.json").c_str(), "w");
    if (js)
    {
        fprintf(js, "{\n  \"width\": %u, \"height\": %u, \"seconds\": %.1f, \"samples\": %u, \"depth\": %u, \"aa\": %u,\n"
                    "  \"coveredTexels\": %zu, \"meanLuminance\": %.6g, \"probes\": %u, \"triangles\": %zu, \"instances\": %u, \"lights\": %u\n}\n",
                w, h, secs, samples, depth, aa, covered, covered ? sumL / covered : 0.0, probesOut, totalTris, nInst, nLights);
        fclose(js);
    }
    printf("fluxbake: %ux%u written to %s (%zu covered texels, %u probes)\n", w, h, outDir.c_str(), covered, probesOut);
    baker->release();
    scene->release();
    return 0;
}
