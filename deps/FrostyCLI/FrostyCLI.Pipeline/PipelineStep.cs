using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Pipeline
{
    public class PipelineStep
    {
        [JsonProperty("op")]              public string Op             { get; set; }
        [JsonProperty("file")]            public string File           { get; set; }
        [JsonProperty("ebx")]             public string Ebx            { get; set; }
        [JsonProperty("delete_groups")]   public bool   DeleteGroups   { get; set; }
        [JsonProperty("remove")]          public bool   Remove         { get; set; }
        [JsonProperty("members")]         public string Members        { get; set; }
        [JsonProperty("template_group")]  public int    TemplateGroup  { get; set; }
        [JsonProperty("keep_physics")]    public bool   KeepPhysics    { get; set; }
        [JsonProperty("member_stub")]     public string MemberStub    { get; set; }
        [JsonProperty("framed")]          public bool   Framed        { get; set; }
        [JsonProperty("no_collision")]    public bool   NoCollision    { get; set; }
        [JsonProperty("as_members")]      public bool   AsMembers      { get; set; }
        [JsonProperty("keep_types")]      public string[] KeepTypes    { get; set; }
        [JsonProperty("only_types")]      public string[] OnlyTypes    { get; set; }
        [JsonProperty("with_resources")]  public bool   WithResources  { get; set; }
        [JsonProperty("only")]            public string Only           { get; set; }
        [JsonProperty("deep")]            public bool   Deep           { get; set; }
        [JsonProperty("add_missing")]     public bool   AddMissing     { get; set; }
        [JsonProperty("as_rigid")]        public bool   AsRigid        { get; set; }
        [JsonProperty("all_parts")]       public bool   AllParts       { get; set; }
        [JsonProperty("target-object")]   public string TargetObject   { get; set; }
        [JsonProperty("blueprint_map")]   public string BlueprintMap   { get; set; }
        [JsonProperty("donor")]           public string Donor          { get; set; }
        [JsonProperty("donor_material")]  public int?   DonorMaterial  { get; set; }
        [JsonProperty("out")]             public string Out            { get; set; }
        [JsonProperty("names")]           public string Names          { get; set; }
        [JsonProperty("field")]           public string Field          { get; set; }
        [JsonProperty("value")]           public JToken Value          { get; set; }
        [JsonProperty("format")]          public string Format         { get; set; }
        [JsonProperty("bundle")]          public string Bundle         { get; set; }
        [JsonProperty("superbundle")]     public string SuperBundle    { get; set; }
        [JsonProperty("object")]          public string Object         { get; set; }
        // ebx-set: apply to every partition object of this type (see `ebx set --each-type`).
        [JsonProperty("each-type")]       public string EachType       { get; set; }
        [JsonProperty("mod-name")]        public string ModName        { get; set; }
        [JsonProperty("mod-author")]      public string ModAuthor      { get; set; }
        [JsonProperty("mod-version")]     public string ModVersion     { get; set; }
        [JsonProperty("mod-description")] public string ModDescription { get; set; }
        [JsonProperty("on-error")]        public string OnError        { get; set; } = "fail";

        // Phase 4 additions
        [JsonProperty("prop")]      public string Prop      { get; set; }
        [JsonProperty("source")]    public string Source    { get; set; }
        [JsonProperty("as")]        public string AsPath    { get; set; }
        [JsonProperty("blueprint")] public string Blueprint { get; set; }
        [JsonProperty("pos")]       public string Pos       { get; set; }
        [JsonProperty("rot")]       public string Rot       { get; set; }
        [JsonProperty("scale")]     public string Scale     { get; set; }

        // Phase 5 additions
        [JsonProperty("no_resolve")] public bool   NoResolve  { get; set; }
        [JsonProperty("regex")]      public bool   Regex      { get; set; }
        [JsonProperty("depth")]      public int?   Depth      { get; set; }
        [JsonProperty("limit")]      public int?   Limit      { get; set; }
        [JsonProperty("type")]       public string Type       { get; set; }
        [JsonProperty("chunk_id")]   public string ChunkId    { get; set; }
        [JsonProperty("res")]        public string Res        { get; set; }
        [JsonProperty("guid")]       public string Guid       { get; set; }

        // Phase 6 additions
        [JsonProperty("target")]         public string Target        { get; set; }
        [JsonProperty("class_guid")]     public string ClassGuid     { get; set; }
        [JsonProperty("target_file_guid")] public string TargetFileGuid { get; set; }
        [JsonProperty("instance_guid")]  public string InstanceGuid  { get; set; }
        [JsonProperty("template")]       public string Template      { get; set; }
        [JsonProperty("mesh")]           public string Mesh          { get; set; }
        [JsonProperty("from_blueprint")]  public string FromBlueprint  { get; set; }
        [JsonProperty("to_blueprint")]    public string ToBlueprint    { get; set; }
        [JsonProperty("group_res")]       public string GroupRes       { get; set; }
        [JsonProperty("transform")]       public string TransformText  { get; set; }
        [JsonProperty("if_centroid_near")] public string IfCentroidNear { get; set; }
        [JsonProperty("material")]       public string Material      { get; set; }
        [JsonProperty("like")]           public string Like          { get; set; }
        [JsonProperty("mvdb")]           public string Mvdb          { get; set; }
        [JsonProperty("group")]          public string Group         { get; set; }
        // NOTE: "flags" is declared once, as uint?, further down with the entity ops. It used to be
        // declared here as a string as well, which made PipelineStep fail to compile (CS0102) once
        // both sets of ops landed together.
        // Realm/property-type name the bits inside a PropertyConnection's packed Flags value.
        [JsonProperty("variation_hash")] public string VariationHash { get; set; }
        [JsonProperty("key")]            public string Key           { get; set; }

        // Phase 7 additions
        [JsonProperty("no_mipmaps")]     public bool   NoMipmaps     { get; set; }

        // import-sound additions
        [JsonProperty("play_on_creation")] public bool? PlayOnCreation { get; set; }
        [JsonProperty("graph_map")] public string GraphMap { get; set; }
        [JsonProperty("flatten")] public bool Flatten { get; set; }
        [JsonProperty("variation")]      public int?   Variation     { get; set; }
        [JsonProperty("encoding")]       public string Encoding      { get; set; }

        // res-import / chunk-import additions
        [JsonProperty("new")]            public bool   New           { get; set; }

        // ebx-array-add / ebx-array-remove additions
        [JsonProperty("index")]          public int?   Index         { get; set; }

        // level-add-entity
        [JsonProperty("set")]             public JObject Set            { get; set; }
        [JsonProperty("transform_field")] public string  TransformField { get; set; }
        [JsonProperty("from")]            public string  From           { get; set; }
        [JsonProperty("intensity_scale")] public float?  IntensityScale { get; set; }
        [JsonProperty("offset")]          public string  Offset         { get; set; }
        [JsonProperty("skip_disabled")]   public bool    SkipDisabled   { get; set; }

        // cross-game port operations
        [JsonProperty("into")]                    public string Into                  { get; set; }
        [JsonProperty("registry")]                public string Registry              { get; set; }
        [JsonProperty("template_prefix")]         public string TemplatePrefix        { get; set; }
        [JsonProperty("template_list")]           public string TemplateList          { get; set; }
        [JsonProperty("member")]                  public string Member                { get; set; }
        [JsonProperty("expect")]                  public string Expect                { get; set; }
        [JsonProperty("prune_prefix")]            public string PrunePrefix           { get; set; }
        [JsonProperty("geo")]                     public string Geo                   { get; set; }
        [JsonProperty("resident")]                public string Resident              { get; set; }
        [JsonProperty("no_geo")]                  public bool   NoGeo                 { get; set; }
        [JsonProperty("allow_foreign_donors")]    public bool   AllowForeignDonors    { get; set; }
        [JsonProperty("luminance_scale")]         public float? LuminanceScale        { get; set; }
        [JsonProperty("luminance_params")]        public string LuminanceParams       { get; set; }
        [JsonProperty("max_lods")]                public int    MaxLods               { get; set; }
        [JsonProperty("shader_fallback")]         public string ShaderFallback        { get; set; }
        [JsonProperty("shader_map")]              public string ShaderMap             { get; set; }
        [JsonProperty("mesh_template")]           public string MeshTemplate          { get; set; }
        [JsonProperty("blueprint_template")]      public string BlueprintTemplate     { get; set; }
        [JsonProperty("physics_template")]        public string PhysicsTemplate       { get; set; }
        [JsonProperty("ported_prefix")]           public string PortedPrefix          { get; set; }
        [JsonProperty("keep_template_materials")] public bool   KeepTemplateMaterials { get; set; }
        [JsonProperty("keep_composite")]          public bool   KeepComposite         { get; set; }   // port-import: a composite source into a composite template (part indices kept)
        [JsonProperty("keep_material_id")]        public bool   KeepMaterialId        { get; set; }
        [JsonProperty("no_physics")]              public bool   NoPhysics             { get; set; }
        [JsonProperty("prefer_ported")]           public bool   PreferPorted          { get; set; }
        [JsonProperty("only_ported")]             public bool   OnlyPorted            { get; set; }
        [JsonProperty("exclude_guid")]            public bool   ExcludeGuid           { get; set; }
        [JsonProperty("exclude_list")]            public string ExcludeList           { get; set; }
        [JsonProperty("no_bundle")]               public bool   NoBundle              { get; set; }
        [JsonProperty("no_radiosity")]            public bool   NoRadiosity           { get; set; }
        [JsonProperty("skip_bundle_types")]       public string SkipBundleTypes       { get; set; }
        [JsonProperty("entity_class")]            public string EntityClass           { get; set; }
        [JsonProperty("flags")]                   public uint?  Flags                 { get; set; }
        [JsonProperty("blueprint_type")]          public string BlueprintType         { get; set; }

        // connection authoring (connection-add / connection-remove)
        [JsonProperty("kind")]                    public string Kind                  { get; set; }
        [JsonProperty("source_field")]            public string SourceField           { get; set; }
        [JsonProperty("target_field")]            public string TargetField           { get; set; }
        [JsonProperty("realm")]                   public string Realm                 { get; set; }
        [JsonProperty("property_type")]           public string PropertyType          { get; set; }
        [JsonProperty("non_static_source")]       public bool   NonStaticSource       { get; set; }
        [JsonProperty("on")]                      public string On                    { get; set; }

        // terrain operations
        [JsonProperty("terrain")]                 public string Terrain               { get; set; }
        [JsonProperty("band")]                    public string Band                  { get; set; }
        [JsonProperty("flat")]                    public string Flat                  { get; set; }
        [JsonProperty("size")]                    public int?   Size                  { get; set; }
        [JsonProperty("keep_derived")]            public bool   KeepDerived           { get; set; }
        [JsonProperty("mask_from")]               public string MaskFrom              { get; set; }
        [JsonProperty("hole_layer")]              public int?   HoleLayer             { get; set; }
        [JsonProperty("hole_dilate")]             public string HoleDilate            { get; set; }
        // mod-diff / mod-test / mod-lint / level-validate / bundle-complete
        [JsonProperty("base")]     public string Base     { get; set; }
        [JsonProperty("level")]    public string Level    { get; set; }
        [JsonProperty("mode")]     public string Mode     { get; set; }
        [JsonProperty("timeout")]  public int?   Timeout  { get; set; }
        [JsonProperty("strict")]   public bool   Strict   { get; set; }
        [JsonProperty("unsealed")] public bool   Unsealed { get; set; }
        [JsonProperty("cosmetic")] public bool   Cosmetic { get; set; }
        // terrain-create (terrain, out, as, offset reuse the fields above)
        [JsonProperty("heights")]      public string   Heights     { get; set; }
        [JsonProperty("layers")]       public string   Layers      { get; set; }
        [JsonProperty("host")]         public string   Host        { get; set; }
        [JsonProperty("colormap")]     public string   Colormap    { get; set; }
        [JsonProperty("color")]        public string   Color       { get; set; }
        [JsonProperty("holes")]        public string   Holes       { get; set; }
        [JsonProperty("hole_below")]   public string   HoleBelow   { get; set; }
        [JsonProperty("world_size")]   public string   WorldSize   { get; set; }
        [JsonProperty("center")]       public string   Center      { get; set; }
        [JsonProperty("origin")]       public string   Origin      { get; set; }
        [JsonProperty("height_range")] public string   HeightRange { get; set; }
        [JsonProperty("rows")]         public string   Rows        { get; set; }
        [JsonProperty("spacing")]      public string   Spacing     { get; set; }
        [JsonProperty("world_y")]      public string   WorldY      { get; set; }
        [JsonProperty("tolerance")]    public string   Tolerance   { get; set; }
        [JsonProperty("budget")]       public int?     Budget      { get; set; }
        [JsonProperty("no_align")]     public bool     NoAlign     { get; set; }
        [JsonProperty("clear_decals")] public bool     ClearDecals { get; set; }
        [JsonProperty("report")]       public string   Report      { get; set; }
        [JsonProperty("args")]         public string[] Args        { get; set; }
    }
}
