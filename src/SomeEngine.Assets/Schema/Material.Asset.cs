using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

public abstract partial class Material
{
    /// <summary>Editable material values and explicit parent overrides.</summary>
    public partial class Asset : global::SomeEngine.Assets.Asset<Material>
    {
        private SharedData<Fields> _fields;
        private sealed class Fields
        {
            internal Guid MaterialType;
            internal AssetId<Shader> Shader;
            internal AssetId<Material> Parent;
            internal List<MaterialParameter> Parameters = [];
            internal Fields Copy() => new() { MaterialType = MaterialType, Shader = Shader, Parent = Parent, Parameters = [.. Parameters] };
        }

        public const string FileType = "SomeEngine.Assets.Schema.Material";
        public const string PathSuffix = ".material.asset";
        public static ulong SchemaHash => MaterialData.SchemaFingerprint;
        public static BinaryWireTypeDescriptor WireType => new(MaterialData.TypeId, SchemaHash, MaterialData.Compatibility, MaterialData.SchemaEpoch);

        public Asset(AssetId<Material> id = default) : this(id, Guid.Empty) { }
        protected Asset(AssetId<Material> id, Guid materialType) : base(id) => _fields = new(new() { MaterialType = materialType });
        protected Asset(Asset source, AssetId<Material> id) : base(source, id) => _fields = source._fields.Retain();
        public Guid MaterialTypeId { get { lock (Access) { RequireIdle(); return _fields.Read.MaterialType; } } }
        public AssetId<Shader> Shader
        {
            get { lock (Access) { RequireIdle(); return _fields.Read.Shader; } }
            set { lock (Access) WriteFields().Shader = value; }
        }
        public AssetId<Material> Parent
        {
            get { lock (Access) { RequireIdle(); return _fields.Read.Parent; } }
            set { lock (Access) WriteFields().Parent = value; }
        }
        private Fields WriteFields()
        {
            RequireIdle();
            if (!_fields.IsShared) return _fields.Read;
            Fields copy = _fields.Read.Copy();
            _fields.Release(out _);
            _fields = new(copy);
            return copy;
        }
        protected override void ReleaseFields() => _fields.Release(out _);
        public bool HasOverride(string property) => GetParameter(property).HasValue;
        public bool Reset(string property) { lock (Access) return WriteFields().Parameters.RemoveAll(p => p.Name == property) != 0; }
        protected MaterialParameter? GetParameter(string name)
        {
            lock (Access)
            {
                RequireIdle();
                foreach (MaterialParameter parameter in _fields.Read.Parameters)
                    if (parameter.Name == name) return parameter;
                return null;
            }
        }
        protected void SetParameter(MaterialParameter value)
        {
            lock (Access)
            {
                List<MaterialParameter> parameters = WriteFields().Parameters;
                int index = parameters.FindIndex(p => p.Name == value.Name);
                if (index < 0) parameters.Add(value); else parameters[index] = value;
            }
        }
        public virtual Asset Duplicate(AssetId<Material> id = default) { lock (Access) { RequireIdle(); return new(this, id); } }
        internal MaterialData CopyValues()
        {
            lock (Access)
            {
                RequireIdle();
                Fields fields = _fields.Read;
                return new() { AssetGuid = Id.Value.ToString(), Name = Name, MaterialType = fields.MaterialType,
                    Shader = fields.Shader, Parent = fields.Parent, Parameters = [.. fields.Parameters] };
            }
        }
        public AssetEntry Describe()
        {
            lock (Access) { RequireIdle(); return Describe(FileType, SchemaHash, References()); }
        }
        private IEnumerable<Guid> References()
        {
            Fields fields = _fields.Read;
            yield return fields.Shader.Value;
            yield return fields.Parent.Value;
            foreach (MaterialParameter parameter in fields.Parameters)
                if (parameter.Kind == MaterialParameterKind.Texture) yield return parameter.Texture.Value;
        }
        public async ValueTask<AssetEntry> SaveAsync(string path, CancellationToken cancellationToken = default)
        {
            using Asset copy = Duplicate();
            MaterialData values = copy.CopyValues();
            return await copy.SaveAsync(BinaryDocumentWriter.Create(values), path, PathSuffix, copy.Describe(), cancellationToken).ConfigureAwait(false);
        }
        private static Asset Read(BinaryDocument document, string path, AssetId<Material> expected = default)
        {
            MaterialData values = document.ReadRoot<MaterialData>();
            if (!Guid.TryParse(values.AssetGuid, out Guid id) || id == Guid.Empty || (!expected.IsEmpty && id != expected.Value))
                throw new InvalidDataException("The material file has a different or empty ID.");
            Asset asset;
            lock (TypeGate)
                asset = values.MaterialType == Guid.Empty ? new(new AssetId<Material>(id))
                    : Types.TryGetValue(values.MaterialType, out var constructors) ? constructors.Asset(new(id))
                    : throw new InvalidDataException($"Unknown material type '{values.MaterialType}'.");
            asset.ReleaseFields();
            asset._fields = new(new() { MaterialType = values.MaterialType, Shader = values.Shader,
                Parent = values.Parent, Parameters = values.Parameters });
            asset.Name = values.Name ?? "";
            asset.AttachSource(document);
            asset.SetSourcePath(path);
            return asset;
        }
        public static async ValueTask<Asset> OpenAsync(AssetLoader loader, AssetId<Material> id, CancellationToken cancellationToken = default)
        {
            var source = await loader.OpenSourceAsync(id, FileType, WireType, cancellationToken).ConfigureAwait(false);
            await using var document = source.Document;
            return Read(document, source.Path, id);
        }
        public static async ValueTask<Asset> OpenAsync(string path, CancellationToken cancellationToken = default)
        {
            await using var document = await OpenFileAsync(path, PathSuffix, WireType, cancellationToken).ConfigureAwait(false);
            return Read(document, Path.GetFullPath(path));
        }
        public ValueTask<Material> LoadAsync(AssetLoader loader, CancellationToken cancellationToken = default)
            => CreateAsync(this, loader, cancellationToken);
    }
}
