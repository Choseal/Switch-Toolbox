using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.IO;
using System.Drawing;
using System.Windows.Forms;
using Toolbox.Library;
using Toolbox.Library.IO;
using Toolbox.Library.Forms;
using OpenTK;
using GL_EditorFramework.EditorDrawables;

namespace FirstPlugin
{
    // Viewer/editor for BotW's ELink2DB.belnk (xlink2, big-endian, version 0x1E).
    // The format class IS the root tree node (like PTCL): users become explorer folders, the
    // AssetCallTable tree nests under them, and clicking an asset leaf opens ELinkAssetEditor.
    // Parser is a C# port of the elink_full.py decode. B1: in-place scalar editing of Direct overrides.
    public class ELink2DB : TreeNodeFile, IFileFormat, IContextMenuNode
    {
        public FileType FileType { get; set; } = FileType.Effect;
        public bool CanSave { get; set; }
        public string[] Description { get; set; } = new string[] { "Effect Link DB (ELink2)" };
        public string[] Extension { get; set; } = new string[] { "*.belnk", "*.sbelnk" };
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public IFileInfo IFileInfo { get; set; }
        public Type[] Types { get { return new Type[0]; } }

        public bool Identify(Stream stream)
        {
            long start = stream.Position;
            try
            {
                using (var reader = new FileReader(stream, true))
                {
                    if (!reader.CheckSignature(4, "XLNK"))
                        return false;
                    reader.ByteOrder = Syroot.BinaryData.ByteOrder.BigEndian;
                    reader.Position = 8;
                    return reader.ReadUInt32() == 0x1E; // BotW Wii U ELink2 (big-endian v30). Switch/TotK is little-endian and not handled here.
                }
            }
            finally { if (stream.CanSeek) stream.Position = start; } // leave the stream as we found it for the next Identify / Load
        }

        public ELinkFile Elink;

        public void Load(Stream stream)
        {
            Text = FileName;
            CanSave = true;   // B1: in-place scalar edits write back (toolbox re-applies Yaz0 from IFileInfo.FileCompression)
            if (stream.CanSeek) stream.Position = 0;   // Identify leaves the stream mid-read; ToArray() copies from the current position
            Elink = new ELinkFile(stream.ToArray());

            // Optional names sidecar: "<file>.names.txt", one user name per line. Each is hashed (CRC32) and
            // matched against the file's stored user hashes; unmatched names are ignored. Absent -> users show as hashes.
            if (!string.IsNullOrEmpty(FilePath) && System.IO.File.Exists(FilePath + ".names.txt"))
                Elink.LoadNames(System.IO.File.ReadAllLines(FilePath + ".names.txt"));

            RebuildUsers();
        }

        // Rebuild the per-actor folders. Actor indices shift when actors are added or removed, so the whole list
        // is rebuilt after an actor op rather than patched.
        public void RebuildUsers()
        {
            Nodes.Clear();
            foreach (int ui in Enumerable.Range(0, (int)Elink.NumUser).OrderBy(i => Elink.UserDisplay(i), StringComparer.OrdinalIgnoreCase))
                Nodes.Add(new ELinkUserNode(Elink, ui));
        }
        public void SelectUser(int ui)
        {
            foreach (TreeNode n in Nodes)
                if (n is ELinkUserNode un && un.Ui == ui) { if (TreeView != null) { TreeView.SelectedNode = n; n.EnsureVisible(); } return; }
        }
        public ToolStripItem[] GetContextMenuItems()
        {
            return new ToolStripItem[]
            {
                new ToolStripMenuItem("Save", null, (s, e) => SaveAction()),
                new ToolStripMenuItem("Add actor...", null, (s, e) => DoCreateActor()),
            };
        }
        void SaveAction()
        {
            var sfd = new SaveFileDialog { Filter = Utils.GetAllFilters(this), FileName = FileName };
            if (sfd.ShowDialog() == DialogResult.OK) STFileSaver.SaveFileFormat(this, sfd.FileName);
        }
        void DoCreateActor()
        {
            string name = ELinkNameDialog.Ask("Add actor", "Actor name");
            if (name == null) return;
            int nu; string err = Elink.CreateUser(name, out nu);
            if (!string.IsNullOrEmpty(err)) { MessageBox.Show(err, "ELink"); return; }
            RebuildUsers(); SelectUser(nu);
        }

        public void Unload() { }
        public void Save(Stream stream)
        {
            var b = Elink.Bytes;            // STFileSaver re-compresses to .sbelnk on save
            stream.Write(b, 0, b.Length);
            if (Elink.NamesDirty && !string.IsNullOrEmpty(FilePath)) WriteNamesSidecar();
        }
        // The .belnk stores only a CRC32 of each actor name, so custom names (added or renamed actors) are written
        // to a "<file>.names.txt" sidecar, merged non-destructively with any existing one. The viewer reads it on load.
        void WriteNamesSidecar()
        {
            string path = FilePath + ".names.txt";
            var lines = new List<string>(); var seen = new HashSet<string>();
            try { if (System.IO.File.Exists(path)) foreach (var l in System.IO.File.ReadAllLines(path)) if (seen.Add(l)) lines.Add(l); } catch { }
            foreach (var nm in Elink.AllNames) if (seen.Add(nm)) lines.Add(nm);
            try { System.IO.File.WriteAllLines(path, lines); } catch { }
        }
    }

    // ---- decoded model -------------------------------------------------------------------------
    public class ELinkOverride
    {
        public string Name;
        public int Bit;          // ParamDefine asset-param index
        public int Type;         // ParamType: 0 Int,1 Float,2 Bool,3 Enum,4 String,5 Custom/Bitfield
        public int RefType;      // packed-ref high byte: 0 Direct,1 String,2 Curve,3 RandomLinear,5 Bitfield
        public string Display;   // human-readable value
        public double Scalar;    // numeric value for Direct/Bitfield (NaN otherwise); feeds the preview multipliers
        public bool Editable;    // Direct float/int/enum/bool -> editable in B1
    }

    // One row in the PropertyGrid: every asset param appears (blank Text = not overridden).
    public class ELinkParamRow
    {
        public int Bit;
        public string Name;
        public int Type;         // ParamType: 0 Int,1 Float,2 Bool,3 Enum,4 String,5 Custom/Bitfield
        public bool IsSet;       // is this param currently overridden?
        public string Text;      // clean editable text ("" when unset)
        public bool Editable;    // false for curve overrides on non-float etc.
        public string Kind;      // "", "random range", "curve", "bitfield" -> shown in the description
        public int Mode;         // float params: 0 NotSet, 1 Constant, 2 Random, 3 Curve (-1 = not a float mode)
        public double Value, Min, Max;   // Constant value, or Random min/max
    }

    public class ELinkCall
    {
        public string Name;
        public uint Guid;
        public bool IsContainer;
        public int ContainerType;     // 0 Switch,1 Random,2 Random2,3 Blend,4 Sequence
        public string ContainerTypeName;
        public string Watch;          // switch watch property
        public string Condition;      // gating condition (null = none / default case)
        public List<ELinkCall> Children;
        public string RuntimeAssetName;                       // emitter set the asset plays (null for containers)
        public uint ParamOffset;                              // asset's offset into the ResParam region (for editing)
        public List<ELinkOverride> Overrides;                 // asset override params (display + edit info)
        public int UserIndex = -1, CallIndex = -1;            // stable identity -> re-locate the asset after the buffer grows
    }

    public class ELinkUserModel
    {
        public List<string> LocalProps = new List<string>();
        public List<KeyValuePair<string, string>> UserParams = new List<KeyValuePair<string, string>>();
        public List<ELinkCall> Tops = new List<ELinkCall>();
        public List<string> TriggerLines = new List<string>();
    }

    // ---- parser (big-endian; port of elink_full.py) --------------------------------------------
    public class ELinkFile
    {
        byte[] D;   // grows on structural edits (copy-on-write block appends)
        public uint Version, NumResParam, NumResAssetParam, NumResTrig, TrigPos, LocPropPos, NumLocProp,
                    NumLocEnum, NumDirect, NumRandom, NumCurve, NumCurvePt, ExRegionPos, NumUser, CondPos, NameTablePos;
        public uint[] UserHashes, UserOffsets;
        public uint AssetParamPos, DVT, RND, CRV, CPT;
        public string[] AssetNames; public int[] AssetTypes;
        public int NumUserParams; string[] UserParamNames; int[] UserParamTypes;
        Dictionary<uint, string> NameMap;

        static readonly string[] CMP = { "==", ">", ">=", "<", "<=", "!=" };
        static readonly string[] PROPT = { "Enum", "S32", "F32", "Bool", "S32", "F32" };
        static readonly string[] CT = { "Switch", "Random", "Random2", "Blend", "Sequence" };

        // big-endian primitives over the fixed buffer
        byte U8(long p) { return D[p]; }
        ushort U16(long p) { return (ushort)((D[p] << 8) | D[p + 1]); }
        short S16(long p) { return (short)U16(p); }
        uint U32(long p) { return ((uint)D[p] << 24) | ((uint)D[p + 1] << 16) | ((uint)D[p + 2] << 8) | D[p + 3]; }
        int I32(long p) { return (int)U32(p); }
        ulong U64(long p) { return ((ulong)U32(p) << 32) | U32(p + 4); }
        float F32(long p) { return BitConverter.ToSingle(new byte[] { D[p + 3], D[p + 2], D[p + 1], D[p] }, 0); }

        string CStr(long p, Encoding enc)
        {
            int e = p < D.Length ? Array.IndexOf(D, (byte)0, (int)p) : (int)p;
            if (e < 0) e = D.Length;
            try { return enc.GetString(D, (int)p, e - (int)p); }
            catch { return Encoding.GetEncoding("ISO-8859-1").GetString(D, (int)p, e - (int)p); }
        }
        // global name pool is UTF-8
        string Name(uint off) { return CStr(NameTablePos + off, Encoding.UTF8); }

        public ELinkFile(byte[] data)
        {
            D = data;

            // ParamDefineTable (static schema: param names/types never change on edit). Its position is derived
            // (0x48 + NumUser*8, right after the UserDataTable), so it shifts if actor entries are added or removed.
            long PDT = 0x48 + (long)U32(0x3C) * 8;
            NumUserParams = (int)U32(PDT + 4);
            int nAsset = (int)U32(PDT + 8);
            int nTrig = (int)U32(PDT + 16);
            long recs = PDT + 20;
            long pdtNames = recs + (NumUserParams + nAsset + nTrig) * 12;
            Func<int, string> pdtStr = off => CStr(pdtNames + off, Encoding.ASCII);

            UserParamNames = new string[NumUserParams]; UserParamTypes = new int[NumUserParams];
            for (int i = 0; i < NumUserParams; i++)
            { long r = recs + i * 12; UserParamNames[i] = pdtStr((int)U32(r)); UserParamTypes[i] = (int)U32(r + 4); }
            AssetNames = new string[nAsset]; AssetTypes = new int[nAsset];
            for (int i = 0; i < nAsset; i++)
            { long r = recs + (NumUserParams + i) * 12; AssetNames[i] = pdtStr((int)U32(r)); AssetTypes[i] = (int)U32(r + 4); }

            Reindex();   // computes AssetParamPos + the value-table positions from the header
        }

        // Re-read every header field, table position and user offset from the (current) buffer. Called after a
        // structural edit grows the file and shifts the regions after the ResParam region.
        void Reindex()
        {
            uint[] h = new uint[17];
            for (int i = 0; i < 17; i++) h[i] = U32(4 + i * 4);
            Version = h[1]; NumResParam = h[2]; NumResAssetParam = h[3]; NumResTrig = h[4];
            TrigPos = h[5]; LocPropPos = h[6]; NumLocProp = h[7]; NumLocEnum = h[8];
            NumDirect = h[9]; NumRandom = h[10]; NumCurve = h[11]; NumCurvePt = h[12];
            ExRegionPos = h[13]; NumUser = h[14]; CondPos = h[15]; NameTablePos = h[16];

            long pdt = 0x48 + (long)NumUser * 8;                    // ParamDefineTable: derived position, moves with NumUser
            AssetParamPos = (uint)((pdt + U32(pdt) + 3) & ~3);      // start of the ResParam region

            UserHashes = new uint[NumUser]; UserOffsets = new uint[NumUser];
            for (int i = 0; i < NumUser; i++) UserHashes[i] = U32(0x48 + i * 4);
            for (int i = 0; i < NumUser; i++) UserOffsets[i] = U32(0x48 + NumUser * 4 + i * 4);

            DVT = LocPropPos + (NumLocProp + NumLocEnum) * 4;
            RND = DVT + NumDirect * 4;
            CRV = RND + NumRandom * 8;
            CPT = CRV + NumCurve * 0x14;
        }

        // ---- user-name labelling (optional) ----
        public void LoadNames(IEnumerable<string> names)
        {
            NameMap = new Dictionary<uint, string>();
            var have = new HashSet<uint>(UserHashes);
            foreach (var raw in names)
            {
                string n = (raw ?? "").Trim();
                if (n.Length == 0) continue;
                uint c = Crc32(n);
                if (have.Contains(c)) NameMap[c] = n;
            }
        }
        public string UserDisplay(int ui)
        {
            uint hash = UserHashes[ui];
            string name;
            if (NameMap != null && NameMap.TryGetValue(hash, out name)) return name;
            return "0x" + hash.ToString("X8");
        }

        static uint[] _crcTab;
        static uint Crc32(string s)
        {
            if (_crcTab == null)
            {
                _crcTab = new uint[256];
                for (uint n = 0; n < 256; n++)
                { uint c = n; for (int k = 0; k < 8; k++) c = ((c & 1) != 0) ? (0xEDB88320 ^ (c >> 1)) : (c >> 1); _crcTab[n] = c; }
            }
            byte[] b = Encoding.UTF8.GetBytes(s);
            uint crc = 0xFFFFFFFF;
            foreach (byte x in b) crc = _crcTab[(crc ^ x) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }

        // ---- value resolution ----
        string ResolveTyped(int defineType, uint reff)
        {
            int rt = (int)(reff >> 24); uint idx = reff & 0xFFFFFF;
            if (rt == 1) return "\"" + Name(idx) + "\"";
            if (rt == 0)
            {
                uint raw = U32(DVT + idx * 4);
                if (defineType == 1) return F32(DVT + idx * 4).ToString();
                if (defineType == 2) return raw != 0 ? "true" : "false";
                if (defineType == 3) return "0x" + raw.ToString("X");
                return ((int)raw).ToString();
            }
            if (rt == 5) return "0b" + Convert.ToString((int)U32(DVT + idx * 4), 2);
            if (rt == 2) return ExpandCurve(idx);
            if (rt == 3 || (rt >= 6 && rt <= 0x11))
            { long r = RND + idx * 8; return "RANDOM[" + F32(r) + " .. " + F32(r + 4) + "]"; }
            if (rt == 4) return "ArrangeParam@0x" + idx.ToString("X");
            return "tag0x" + rt.ToString("X") + "[" + idx + "]";
        }

        // One representative numeric value for an override, for the preview multiplier: the scalar itself for a direct
        // value, the midpoint for a random range, the first curve control-point value. NaN for non-numeric (string, etc.).
        double ScalarOf(int defineType, uint reff)
        {
            int rt = (int)(reff >> 24); uint idx = reff & 0xFFFFFF;
            if (rt == 0) return defineType == 1 ? F32(DVT + idx * 4) : (double)(int)U32(DVT + idx * 4);
            if (rt == 5) return (int)U32(DVT + idx * 4);
            if (rt == 2) { long c = CRV + idx * 0x14; int npts = U16(c + 2), pstart = U16(c); return npts > 0 ? F32(CPT + pstart * 8 + 4) : 1.0; }
            if (rt == 3 || (rt >= 6 && rt <= 0x11)) { long r = RND + idx * 8; return (F32(r) + F32(r + 4)) / 2.0; }
            return double.NaN;
        }

        // ---- B1: in-place scalar editing (no relayout) -----------------------------------------
        // Override values aren't stored per-param; a ResParam is a 4-byte ref into the SHARED directValueTable, so
        // most values are deduplicated across many params. To edit ONE param without disturbing the others we give it
        // its own directValueTable entry (a spare/unreferenced slot) and repoint just this ResParam. All edits are
        // byte-for-byte in place: directValueTable + ResParam region are fixed-size, so file size never changes.
        public bool Dirty;
        public byte[] Bytes { get { return D; } }
        void WU32(long p, uint v) { D[p] = (byte)(v >> 24); D[p + 1] = (byte)(v >> 16); D[p + 2] = (byte)(v >> 8); D[p + 3] = (byte)v; }
        static uint FloatBits(float f) { var b = BitConverter.GetBytes(f); return ((uint)b[3] << 24) | ((uint)b[2] << 16) | ((uint)b[1] << 8) | b[0]; }
        static int Popcount(ulong m) { int c = 0; while (m != 0) { c += (int)(m & 1); m >>= 1; } return c; }

        Dictionary<int, int> _refCount, _randRefCount; List<int> _spares; bool _editInit;
        void InitEdit()
        {
            if (_editInit) return; _editInit = true;
            _refCount = new Dictionary<int, int>(); _randRefCount = new Dictionary<int, int>();
            long p = AssetParamPos;
            for (int s = 0; s < NumResAssetParam; s++)
            {
                ulong m = U64(p); p += 8;
                int nb = Popcount(m);
                for (int k = 0; k < nb; k++)
                {
                    uint r = U32(p); p += 4; int rt = (int)(r >> 24); int idx = (int)(r & 0xFFFFFF);
                    if (rt == 0 || rt == 5) _refCount[idx] = (_refCount.TryGetValue(idx, out int c) ? c : 0) + 1;
                    else if (rt == 3 || (rt >= 6 && rt <= 0x11)) _randRefCount[idx] = (_randRefCount.TryGetValue(idx, out int rc) ? rc : 0) + 1;
                }
            }
            _spares = new List<int>();
            for (int i = 0; i < (int)NumDirect; i++) if (!_refCount.ContainsKey(i)) _spares.Add(i);
        }
        // Byte offset of the ResParam ref for asset-param `bit` of the block at `paramOffset`; -1 if not overridden.
        long RefPos(uint paramOffset, int bit)
        {
            long bs = AssetParamPos + paramOffset; ulong mask = U64(bs);
            if (((mask >> bit) & 1) == 0) return -1;
            return bs + 8 + Popcount(mask & (((ulong)1 << bit) - 1)) * 4;
        }
        void Repoint(long rp, int oldIdx, int newIdx, int rt)
        {
            WU32(rp, ((uint)rt << 24) | (uint)newIdx);
            if (_refCount.TryGetValue(oldIdx, out int oc)) _refCount[oldIdx] = oc - 1;
            _refCount[newIdx] = (_refCount.TryGetValue(newIdx, out int nc) ? nc : 0) + 1;
        }
        // Set the Direct/Bitfield override `bit` of the asset at `paramOffset` to `value`, in place. Returns "" on
        // success or a short reason it couldn't (string/curve/random not editable here; or no spare slot left).
        public string SetDirectValue(uint paramOffset, int bit, int defineType, double value)
        {
            InitEdit();
            long rp = RefPos(paramOffset, bit);
            if (rp < 0) return "param is not overridden";
            uint cur = U32(rp); int rt = (int)(cur >> 24);
            if (rt != 0 && rt != 5) return "only direct values are editable here (string / curve / random need structural edit)";
            uint bits = (defineType == 1) ? FloatBits((float)value) : unchecked((uint)(int)Math.Round(value));
            int oldIdx = (int)(cur & 0xFFFFFF);
            if (_refCount.TryGetValue(oldIdx, out int rc) && rc == 1) { WU32(DVT + oldIdx * 4, bits); Dirty = true; return ""; }   // param owns its slot
            for (int i = 0; i < (int)NumDirect; i++)                                                                              // reuse an existing entry with this value
                if (_refCount.ContainsKey(i) && U32(DVT + i * 4) == bits) { Repoint(rp, oldIdx, i, rt); Dirty = true; return ""; }
            if (_spares.Count > 0)                                                                                                // take a spare slot for the new value
            {
                int sp = _spares[_spares.Count - 1]; _spares.RemoveAt(_spares.Count - 1);
                WU32(DVT + sp * 4, bits); Repoint(rp, oldIdx, sp, rt); Dirty = true; return "";
            }
            return "no spare value slot left for a new value (needs structural edit / B2)";
        }

        // ---- B2: structural override editing (copy-on-write) -----------------------------------
        // Override blocks are SHARED: identical override-sets are de-duplicated, so one block can back hundreds of
        // assets. To edit/add/remove an override for ONE asset without touching its block-mates, we give that asset a
        // PRIVATE copy of the block (appended at the end of the ResParam region) and repoint only its call-table entry.
        // Append-at-end means existing block offsets never move, so everything we don't rebuild stays byte-verbatim
        // (the TriggerOverwrite table, sorted tables and conditions keep their offsets).

        void InsertBytes(long at, byte[] ins)
        {
            var nd = new byte[D.Length + ins.Length];
            Array.Copy(D, 0, nd, 0, (int)at);
            Array.Copy(ins, 0, nd, (int)at, ins.Length);
            Array.Copy(D, (int)at, nd, (int)at + ins.Length, D.Length - (int)at);
            D = nd;
        }
        long RegionEnd()   // first byte past the last ResParam block
        {
            long p = AssetParamPos;
            for (int s = 0; s < NumResAssetParam; s++) p += 8 + Popcount(U64(p)) * 4;
            return p;
        }
        // ACT entry offset for call `ci` of user `ui`, in the current buffer.
        long LocateACT(int ui, int ci)
        {
            long b = UserOffsets[ui]; int nLocal = (int)U32(b + 4), nCall = (int)U32(b + 8);
            long p = b + 0x30 + nLocal * 4 + NumUserParams * 4;
            p += ((nCall * 2) + 3) & ~3;        // skip sortedAssetIdTable
            return p + ci * 0x20;
        }
        uint ParamOffsetOf(int ui, int ci) { return U32(LocateACT(ui, ci) + 0x18); }
        void RepointAsset(int ui, int ci, uint paramOffset) { WU32(LocateACT(ui, ci) + 0x18, paramOffset); }

        int CountSharers(uint paramOffset)   // how many asset call-entries (any user) point to this block
        {
            int n = 0;
            for (int ui = 0; ui < NumUser; ui++)
            {
                long b = UserOffsets[ui]; int nLocal = (int)U32(b + 4), nCall = (int)U32(b + 8);
                long ACT = b + 0x30 + nLocal * 4 + NumUserParams * 4 + (((nCall * 2) + 3) & ~3);
                for (int c = 0; c < nCall; c++) { long a = ACT + c * 0x20; if ((U16(a + 6) & 1) == 0 && U32(a + 0x18) == paramOffset) n++; }
            }
            return n;
        }
        uint[] ReadBlockRefs(uint paramOffset, out ulong mask)
        {
            long bs = AssetParamPos + paramOffset; mask = U64(bs);
            int n = Popcount(mask); var refs = new uint[n];
            for (int k = 0; k < n; k++) refs[k] = U32(bs + 8 + k * 4);
            return refs;
        }
        // A directValueTable index holding `value`: reuse an existing one, else claim a spare slot. -1 if none free.
        int ResolveDirectIndex(int type, double value)
        {
            InitEdit();
            uint bits = (type == 1) ? FloatBits((float)value) : unchecked((uint)(int)Math.Round(value));
            for (int i = 0; i < (int)NumDirect; i++) if (_refCount.ContainsKey(i) && U32(DVT + i * 4) == bits) return i;
            if (_spares.Count > 0) { int sp = _spares[_spares.Count - 1]; _spares.RemoveAt(_spares.Count - 1); WU32(DVT + sp * 4, bits); return sp; }
            return -1;
        }
        // Append a fresh override block (mask + refs) at the region end; patch header + table positions; reindex.
        uint AppendBlock(ulong mask, uint[] refs)
        {
            long regionEnd = RegionEnd();
            uint npo = (uint)(regionEnd - AssetParamPos);
            var block = new byte[8 + refs.Length * 4];
            for (int i = 0; i < 8; i++) block[i] = (byte)(mask >> (56 - i * 8));
            for (int k = 0; k < refs.Length; k++)
            { block[8 + k * 4] = (byte)(refs[k] >> 24); block[8 + k * 4 + 1] = (byte)(refs[k] >> 16); block[8 + k * 4 + 2] = (byte)(refs[k] >> 8); block[8 + k * 4 + 3] = (byte)refs[k]; }
            InsertBytes(regionEnd, block);
            uint len = (uint)block.Length;
            WU32(4, U32(4) + len);                          // FileSize
            WU32(0xC, U32(0xC) + (uint)refs.Length);        // NumResParam (total refs)
            WU32(0x10, U32(0x10) + 1);                       // NumResAssetParam (block count)
            foreach (int i in new[] { 5, 6, 13, 15, 16 })    // TrigPos, LocPropPos, ExRegionPos, CondPos, NameTablePos
            { uint old = U32(4 + i * 4); if (old >= regionEnd) WU32(4 + i * 4, old + len); }
            long uoBase = 0x48 + NumUser * 4;                // every exRegion sits after the insertion -> all shift
            for (int i = 0; i < NumUser; i++) WU32(uoBase + i * 4, U32(uoBase + i * 4) + len);
            Reindex();
            _editInit = false;                               // ref-count / spare cache is now stale
            return npo;
        }
        // Give the asset a private (refcount-1) block if it currently shares one; returns the editable paramOffset.
        uint EnsurePrivateBlock(int ui, int ci)
        {
            uint po = ParamOffsetOf(ui, ci);
            if (CountSharers(po) <= 1) return po;            // already private -> edit in place
            var refs = ReadBlockRefs(po, out ulong mask);
            uint npo = AppendBlock(mask, refs);
            RepointAsset(ui, ci, npo);
            return npo;
        }

        // Set an existing override to a Direct scalar (isolated). If the param currently holds a random range or a
        // curve, this replaces it with a constant (the old random/curve entry is simply left unreferenced).
        public string EditValue(int ui, int ci, int bit, int type, double value)
        {
            uint po = EnsurePrivateBlock(ui, ci);
            long rp = RefPos(po, bit);
            if (rp < 0) return "param is not overridden";
            int rt = (int)(U32(rp) >> 24);
            if (rt == 0 || rt == 5) return SetDirectValue(po, bit, type, value);   // Direct/Bitfield: spare-slot path
            int di = ResolveDirectIndex(type, value);                              // random/curve -> point at a constant
            if (di < 0) return "no spare value slot left (needs directValueTable growth)";
            WU32(rp, (uint)di); Dirty = true; return "";
        }

        // ---- random-range editing (Float params only) ------------------------------------------
        void WriteRand(int idx, float min, float max) { WU32(RND + idx * 8, FloatBits(min)); WU32(RND + idx * 8 + 4, FloatBits(max)); }
        // Append one (min,max) entry to the randomTable (it sits mid-file, so the curve/point/exRegion/condition/name
        // regions after it shift). Returns the new index.
        uint GrowRandom(float min, float max)
        {
            long insAt = RND + NumRandom * 8; uint newIdx = NumRandom;
            InsertBytes(insAt, new byte[8]);
            WU32(insAt, FloatBits(min)); WU32(insAt + 4, FloatBits(max));
            WU32(4, U32(4) + 8);                              // FileSize
            WU32(4 + 10 * 4, NumRandom + 1);                  // numRandom (h10)
            foreach (int i in new[] { 13, 15, 16 })           // ExRegionPos, CondPos, NameTablePos (after the random table)
            { uint old = U32(4 + i * 4); if (old >= insAt) WU32(4 + i * 4, old + 8); }
            long uoBase = 0x48 + NumUser * 4;
            for (int i = 0; i < NumUser; i++) WU32(uoBase + i * 4, U32(uoBase + i * 4) + 8);
            Reindex(); _editInit = false;
            return newIdx;
        }
        // A randomTable index holding (min,max): reuse a referenced identical entry, else grow the table.
        int ResolveRandomIndex(float min, float max)
        {
            InitEdit();
            uint mb = FloatBits(min), xb = FloatBits(max);
            for (int i = 0; i < (int)NumRandom; i++)
                if (_randRefCount.ContainsKey(i) && U32(RND + i * 8) == mb && U32(RND + i * 8 + 4) == xb) return i;
            return (int)GrowRandom(min, max);
        }
        string AddRandom(int ui, int ci, int bit, float min, float max)
        { return AddRefInternal(ui, ci, bit, (3u << 24) | (uint)ResolveRandomIndex(min, max)); }
        string EditRandom(int ui, int ci, int bit, float min, float max)
        {
            uint po = EnsurePrivateBlock(ui, ci);
            long rp = RefPos(po, bit);
            if (rp < 0) return "param is not overridden";
            uint cur = U32(rp); int rt = (int)(cur >> 24), idx = (int)(cur & 0xFFFFFF);
            InitEdit();
            if ((rt == 3 || (rt >= 6 && rt <= 0x11)) && _randRefCount.TryGetValue(idx, out int rc) && rc == 1)
            { WriteRand(idx, min, max); Dirty = true; return ""; }                 // owns its entry -> edit in place
            WU32(rp, (3u << 24) | (uint)ResolveRandomIndex(min, max)); Dirty = true; return "";
        }

        // One entry point for float params: mode 0 = not set (remove), 1 = constant, 2 = random range.
        public string SetFloatParam(int ui, int ci, int bit, int mode, double value, double min, double max)
        {
            bool isSet = ((U64(AssetParamPos + ParamOffsetOf(ui, ci)) >> bit) & 1) != 0;
            if (mode == 0) return isSet ? RemoveOverride(ui, ci, bit) : "";
            if (mode == 1) return isSet ? EditValue(ui, ci, bit, 1, value) : AddOverride(ui, ci, bit, 1, value);
            return isSet ? EditRandom(ui, ci, bit, (float)min, (float)max) : AddRandom(ui, ci, bit, (float)min, (float)max);
        }
        // Insert a new ResParam (any ref) for a not-yet-set bit: rebuild the block with it spliced in, append, repoint.
        string AddRefInternal(int ui, int ci, int bit, uint newref)
        {
            if (bit < 0 || bit >= AssetNames.Length) return "bad parameter";
            var refs = ReadBlockRefs(ParamOffsetOf(ui, ci), out ulong mask);
            if (((mask >> bit) & 1) != 0) return "already overridden";
            int slot = Popcount(mask & (((ulong)1 << bit) - 1));
            var nrefs = new uint[refs.Length + 1];
            Array.Copy(refs, 0, nrefs, 0, slot); nrefs[slot] = newref; Array.Copy(refs, slot, nrefs, slot + 1, refs.Length - slot);
            uint npo = AppendBlock(mask | ((ulong)1 << bit), nrefs);
            RepointAsset(ui, ci, npo);
            return "";
        }
        // Add a not-yet-set Direct override. No default bloat: only this param is written.
        public string AddOverride(int ui, int ci, int bit, int type, double value)
        {
            int di = ResolveDirectIndex(type, value);
            if (di < 0) return "no spare value slot left (needs directValueTable growth)";
            return AddRefInternal(ui, ci, bit, (uint)di);     // refType 0 (Direct)
        }
        public string AddStringOverride(int ui, int ci, int bit, string name)
        { return AddRefInternal(ui, ci, bit, (1u << 24) | ResolveNameOffset(name)); }   // refType 1 (String)
        // Repoint an already-set string override to a (possibly new) name. Copy-on-write keeps it isolated.
        public string SetStringValue(int ui, int ci, int bit, string name)
        {
            uint po = EnsurePrivateBlock(ui, ci);
            long rp = RefPos(po, bit);
            if (rp < 0) return "param is not overridden";
            WU32(rp, (1u << 24) | ResolveNameOffset(name)); Dirty = true; return "";
        }
        // A name pool offset for `s`: reuse an existing entry, else append the string (the pool is the last region,
        // so appending only grows EOF, nothing shifts). Names are referenced relative to NameTablePos.
        uint ResolveNameOffset(string s)
        {
            byte[] want = Encoding.UTF8.GetBytes(s ?? "");
            long p = NameTablePos;
            while (p < D.Length)
            {
                int e = Array.IndexOf(D, (byte)0, (int)p); if (e < 0) e = D.Length;
                if (e - p == want.Length)
                {
                    bool eq = true;
                    for (int k = 0; k < want.Length; k++) if (D[p + k] != want[k]) { eq = false; break; }
                    if (eq) return (uint)(p - NameTablePos);
                }
                p = e + 1;
            }
            uint off = (uint)(D.Length - NameTablePos);
            var add = new byte[want.Length + 1];
            Array.Copy(want, add, want.Length);              // trailing 0 already present
            InsertBytes(D.Length, add);
            WU32(4, U32(4) + (uint)add.Length);              // FileSize (nothing sits after the name pool)
            Reindex();
            return off;
        }

        // One entry point for the editor: blank text removes the override, filled text adds it (if unset) or edits it.
        public string SetParam(int ui, int ci, int bit, int type, string text)
        {
            text = (text ?? "").Trim();
            bool isSet = ((U64(AssetParamPos + ParamOffsetOf(ui, ci)) >> bit) & 1) != 0;
            if (text.Length == 0) return isSet ? RemoveOverride(ui, ci, bit) : "";
            if (type == 4) return isSet ? SetStringValue(ui, ci, bit, text) : AddStringOverride(ui, ci, bit, text);
            double v;
            if (!ParseTyped(type, text, out v)) return "enter a valid " + TypeName(type) + " value";
            return isSet ? EditValue(ui, ci, bit, type, v) : AddOverride(ui, ci, bit, type, v);
        }
        static bool ParseTyped(int type, string s, out double val)
        {
            val = 0; s = (s ?? "").Trim();
            if (type == 1) return double.TryParse(s, out val);                              // float
            if (type == 2)                                                                  // bool
            {
                if (s == "true" || s == "1") { val = 1; return true; }
                if (s == "false" || s == "0") { val = 0; return true; }
                return false;
            }
            int iv;                                                                        // int / enum (0x.. or decimal)
            if (s.StartsWith("0x") || s.StartsWith("0X"))
            { if (int.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out iv)) { val = iv; return true; } return false; }
            if (int.TryParse(s, out iv)) { val = iv; return true; }
            return false;
        }
        static string TypeName(int t) { var n = new[] { "integer", "float", "bool (true/false)", "enum (number)", "text" }; return t >= 0 && t < n.Length ? n[t] : "value"; }
        // Remove an override entirely (the param falls back to its ParamDefine default at runtime).
        public string RemoveOverride(int ui, int ci, int bit)
        {
            uint po = ParamOffsetOf(ui, ci);
            var refs = ReadBlockRefs(po, out ulong mask);
            if (((mask >> bit) & 1) == 0) return "not overridden";
            int slot = Popcount(mask & (((ulong)1 << bit) - 1));
            var nrefs = new uint[refs.Length - 1];
            Array.Copy(refs, 0, nrefs, 0, slot); Array.Copy(refs, slot + 1, nrefs, slot, refs.Length - slot - 1);
            uint npo = AppendBlock(mask & ~((ulong)1 << bit), nrefs);
            RepointAsset(ui, ci, npo);
            return "";
        }
        // Re-read an asset's overrides from the current buffer (after an edit) for the UI to refresh.
        public List<ELinkOverride> ReadOverrides(int ui, int ci)
        {
            var list = new List<ELinkOverride>();
            long bs = AssetParamPos + ParamOffsetOf(ui, ci); ulong mask = U64(bs); long q = bs + 8;
            for (int bit = 0; bit < 64; bit++)
            {
                if (((mask >> bit) & 1) == 0) continue;
                uint reff = U32(q); q += 4;
                if (bit >= AssetNames.Length) continue;
                int rt = (int)(reff >> 24), t = AssetTypes[bit];
                list.Add(new ELinkOverride
                {
                    Name = AssetNames[bit], Bit = bit, Type = t, RefType = rt,
                    Display = ResolveTyped(t, reff), Scalar = ScalarOf(t, reff),
                    Editable = rt == 0 && (t == 0 || t == 1 || t == 2 || t == 3),
                });
            }
            return list;
        }
        // Every asset param as a grid row: blank Text = not overridden. Set params show a CLEAN value (bare strings,
        // "min .. max" for random, "curve(n)" for curves) rather than the raw decode. Non-scalar overrides are read-only.
        public List<ELinkParamRow> ReadAllParams(int ui, int ci)
        {
            var rows = new List<ELinkParamRow>();
            long bs = AssetParamPos + ParamOffsetOf(ui, ci); ulong mask = U64(bs); long q = bs + 8;
            var refByBit = new Dictionary<int, uint>();
            for (int bit = 0; bit < 64; bit++) if (((mask >> bit) & 1) != 0) { if (bit < AssetNames.Length) refByBit[bit] = U32(q); q += 4; }
            for (int bit = 0; bit < AssetNames.Length; bit++)
            {
                int t = AssetTypes[bit];
                var row = new ELinkParamRow { Bit = bit, Name = AssetNames[bit], Type = t };
                if (refByBit.TryGetValue(bit, out uint reff))
                {
                    row.IsSet = true; FormatRef(t, reff, out row.Text, out row.Editable, out row.Kind);
                    int rt = (int)(reff >> 24), idx = (int)(reff & 0xFFFFFF);
                    if (rt == 0) { row.Mode = 1; row.Value = ScalarOf(t, reff); }
                    else if (rt == 3 || (rt >= 6 && rt <= 0x11)) { row.Mode = 2; row.Min = F32(RND + idx * 8); row.Max = F32(RND + idx * 8 + 4); }
                    else if (rt == 2) row.Mode = 3;
                    else row.Mode = -1;     // string / bitfield -> not a float mode
                }
                else
                { row.IsSet = false; row.Text = ""; row.Editable = t <= 4; row.Kind = ""; row.Mode = 0; }   // unset -> blank
                rows.Add(row);
            }
            return rows;
        }
        // Clean value text + editability for one ResParam ref.
        void FormatRef(int type, uint reff, out string text, out bool editable, out string kind)
        {
            int rt = (int)(reff >> 24); uint idx = reff & 0xFFFFFF;
            if (rt == 1) { text = Name(idx); editable = true; kind = ""; return; }            // string -> bare, no quotes
            if (rt == 0)
            {
                uint raw = U32(DVT + idx * 4);
                text = type == 1 ? F32(DVT + idx * 4).ToString() : type == 2 ? (raw != 0 ? "true" : "false") : ((int)raw).ToString();
                editable = true; kind = ""; return;
            }
            if (rt == 5) { text = ((int)U32(DVT + idx * 4)).ToString(); editable = true; kind = "bitfield"; return; }
            // curve / random: scalar-typed params can be edited (typing a number replaces the curve/range with a constant)
            if (rt == 2) { long c = CRV + idx * 0x14; text = "curve (" + U16(c + 2) + " pts)"; editable = type <= 3; kind = "curve, editing sets a constant"; return; }
            if (rt == 3 || (rt >= 6 && rt <= 0x11)) { long r = RND + idx * 8; text = F32(r) + " .. " + F32(r + 4); editable = type <= 3; kind = "random range, editing sets a constant"; return; }
            text = "(ref " + rt + ")"; editable = false; kind = "?"; return;
        }

        List<string> _curLocals;
        string ExpandCurve(uint idx)
        {
            long c = CRV + idx * 0x14;
            int pstart = U16(c), npts = U16(c + 2), ctype = U16(c + 4), isGlobal = U16(c + 6);
            uint propName = U32(c + 8); short propIdx = S16(c + 0x10);
            string tn = ctype == 0 ? "Standard" : (ctype == 1 ? "Constant" : ctype.ToString());
            string prop;
            if (isGlobal != 0) prop = "Global::" + Name(propName);
            else prop = "Local::" + (_curLocals != null && propIdx >= 0 && propIdx < _curLocals.Count ? _curLocals[propIdx] : "#" + propIdx);
            var sb = new StringBuilder("CURVE[" + tn + "] " + prop + " :");
            for (int k = 0; k < npts; k++) sb.Append(" (" + F32(CPT + (pstart + k) * 8) + "," + F32(CPT + (pstart + k) * 8 + 4) + ")");
            return sb.ToString();
        }

        string DecodeCondition(uint coff)
        {
            if (coff == 0xFFFFFFFF) return null;
            long c = CondPos + coff;
            uint pt = U32(c);
            if (pt == 1 || pt == 2) return "weight=" + F32(c + 4);
            if (pt == 4) return "forceContinue=" + U32(c + 4);
            uint propType = U32(c + 4), cmp = U32(c + 8), val = U32(c + 0xC);
            short locEnumIdx = S16(c + 0x10); byte isGlobal = U8(c + 0x13);
            string cmps = cmp < CMP.Length ? CMP[cmp] : cmp.ToString();
            if (propType == 0)
            {
                string v;
                if (isGlobal != 0) v = "\"" + Name(val) + "\"";
                else { long enumTbl = LocPropPos + NumLocProp * 4; v = locEnumIdx >= 0 ? "\"" + Name(U32(enumTbl + locEnumIdx * 4)) + "\"" : "(default)"; }
                return "Enum " + cmps + " " + v;
            }
            string ptn = propType < PROPT.Length ? PROPT[propType] : propType.ToString();
            string val2;
            if (ptn == "F32") val2 = F32(c + 0xC).ToString();
            else if (ptn == "Bool") val2 = val != 0 ? "true" : "false";
            else val2 = ((int)val).ToString();
            return ptn + " " + cmps + " " + val2;
        }

        // =====================================================================================
        //  Node operations: create / delete / duplicate of call nodes (asset leaves and
        //  Switch/Random/Blend/Sequence containers). The edited user's exRegion block is parsed
        //  into a call tree, mutated, then re-laid-out and spliced back. Layout = top-level calls
        //  first, then a DFS pre-order that block-allocates each node's direct children, so every
        //  container child range stays contiguous. The sortedAssetIdTable is regenerated by call
        //  name; trigger range tables (slot->action->trigger, property->trigger) stay INCLUSIVE;
        //  downstream offsets shift like a block append.

        class CallNode
        {
            public byte[] Act;                  // 0x20 ACT entry (parentIndex @0xC and container paramOrContainerOffset @0x18 are rewritten on layout)
            public bool IsContainer;
            public byte[] Rec;                  // container record (ctype + child range + Switch tail); null for a leaf
            public List<CallNode> Children = new List<CallNode>();
            public CallNode Parent;
            public int Old = -1;                // original call index (-1 for a fabricated node) -> trigger remap
            public int New;                     // index assigned by layout
        }
        class TrigAction { public byte[] Rec; public List<byte[]> Trigs = new List<byte[]>(); }
        class TrigSlot { public byte[] Rec; public List<TrigAction> Actions = new List<TrigAction>(); }
        class TrigProp { public byte[] Rec; public List<byte[]> Trigs = new List<byte[]>(); }
        class UserBlock
        {
            public byte[] Prefix, Locals, UParams;
            public int NLocal, NCall;
            public List<CallNode> All = new List<CallNode>();   // existing nodes, by old index
            public List<CallNode> Roots = new List<CallNode>();
            public List<TrigSlot> Slots = new List<TrigSlot>();
            public List<TrigProp> Props = new List<TrigProp>();
            public List<byte[]> Always = new List<byte[]>();
        }

        // big-endian helpers over a standalone byte[] (ACT entries, container records, trigger records)
        static uint RU32(byte[] a, int p) { return ((uint)a[p] << 24) | ((uint)a[p + 1] << 16) | ((uint)a[p + 2] << 8) | a[p + 3]; }
        static int RI32(byte[] a, int p) { return (int)RU32(a, p); }
        static ushort RU16(byte[] a, int p) { return (ushort)((a[p] << 8) | a[p + 1]); }
        static void RW32(byte[] a, int p, uint v) { a[p] = (byte)(v >> 24); a[p + 1] = (byte)(v >> 16); a[p + 2] = (byte)(v >> 8); a[p + 3] = (byte)v; }
        static void RW16(byte[] a, int p, ushort v) { a[p] = (byte)(v >> 8); a[p + 1] = (byte)v; }
        byte[] Slice(long at, int len) { var r = new byte[len]; Array.Copy(D, (int)at, r, 0, len); return r; }
        byte[] NameBytes(uint off)
        {
            long p = NameTablePos + off; int e = Array.IndexOf(D, (byte)0, (int)p); if (e < 0) e = D.Length;
            var r = new byte[e - (int)p]; Array.Copy(D, (int)p, r, 0, r.Length); return r;
        }
        static int CompareBytes(byte[] a, byte[] b)
        { int n = Math.Min(a.Length, b.Length); for (int i = 0; i < n; i++) if (a[i] != b[i]) return a[i] - b[i]; return a.Length - b.Length; }
        long BlockSizeAt(long b)
        {
            int nSlot = (int)U32(b + 0x14), nAction = (int)U32(b + 0x18), nAT = (int)U32(b + 0x1C);
            int nProp = (int)U32(b + 0x20), nPT = (int)U32(b + 0x24), nAlw = (int)U32(b + 0x28);
            return U32(b + 0x2C) + nSlot * 8 + nAction * 0xC + nAT * 0x18 + nProp * 0x10 + nPT * 0x14 + nAlw * 0x10;
        }

        UserBlock ParseUser(int ui)
        {
            long b = UserOffsets[ui];
            int nLocal = (int)U32(b + 4), nCall = (int)U32(b + 8); uint ttp = U32(b + 0x2C);
            var ub = new UserBlock { NLocal = nLocal, NCall = nCall, Prefix = Slice(b, 0x30) };
            long o = b + 0x30;
            ub.Locals = Slice(o, nLocal * 4); o += nLocal * 4;
            ub.UParams = Slice(o, NumUserParams * 4); o += NumUserParams * 4;
            o += ((nCall * 2) + 3) & ~3;                       // sortedAssetIdTable (regenerated)
            long ACT = o, contStart = ACT + nCall * 0x20, contEnd = b + ttp;
            var cpoc = new List<int>();                        // container record offsets -> sizes from sorted gaps
            for (int i = 0; i < nCall; i++) { long a = ACT + i * 0x20; if ((U16(a + 6) & 1) != 0) cpoc.Add((int)U32(a + 0x18)); }
            cpoc.Sort();
            var sizeOf = new Dictionary<int, int>();
            for (int k = 0; k < cpoc.Count; k++) sizeOf[cpoc[k]] = (k + 1 < cpoc.Count ? cpoc[k + 1] : (int)(contEnd - contStart)) - cpoc[k];
            for (int i = 0; i < nCall; i++)
            {
                long a = ACT + i * 0x20; bool isc = (U16(a + 6) & 1) != 0;
                byte[] rec = isc ? Slice(contStart + (int)U32(a + 0x18), sizeOf[(int)U32(a + 0x18)]) : null;
                ub.All.Add(new CallNode { Act = Slice(a, 0x20), IsContainer = isc, Rec = rec, Old = i });
            }
            for (int i = 0; i < nCall; i++)
            {
                if (!ub.All[i].IsContainer) continue;
                int poc = (int)U32(ACT + i * 0x20 + 0x18);
                int cs = I32(contStart + poc + 4), ce = I32(contStart + poc + 8);
                if (cs >= 0) for (int ch = cs; ch <= ce; ch++) { ub.All[i].Children.Add(ub.All[ch]); ub.All[ch].Parent = ub.All[i]; }
            }
            foreach (var n in ub.All) if (n.Parent == null) ub.Roots.Add(n);
            ParseTriggers(b, ub);
            return ub;
        }
        void ParseTriggers(long b, UserBlock ub)
        {
            int nSlot = (int)U32(b + 0x14), nAction = (int)U32(b + 0x18), nAT = (int)U32(b + 0x1C);
            int nProp = (int)U32(b + 0x20), nPT = (int)U32(b + 0x24), nAlw = (int)U32(b + 0x28);
            long tb = b + U32(b + 0x2C);
            long oAct = tb + nSlot * 8, oAT = oAct + nAction * 0xC, oProp = oAT + nAT * 0x18, oPT = oProp + nProp * 0x10, oAlw = oPT + nPT * 0x14;
            var actObjs = new TrigAction[nAction];
            for (int a = 0; a < nAction; a++)
            {
                var ar = Slice(oAct + a * 0xC, 0xC); var ta = new TrigAction { Rec = ar };
                for (int t = RI32(ar, 4); t <= RI32(ar, 8); t++) ta.Trigs.Add(Slice(oAT + t * 0x18, 0x18));
                actObjs[a] = ta;
            }
            for (int s = 0; s < nSlot; s++)
            {
                var sr = Slice(tb + s * 8, 8); var sl = new TrigSlot { Rec = sr };
                for (int a = (short)RU16(sr, 4); a <= (short)RU16(sr, 6); a++) sl.Actions.Add(actObjs[a]);
                ub.Slots.Add(sl);
            }
            for (int p = 0; p < nProp; p++)
            {
                var pr = Slice(oProp + p * 0x10, 0x10); var pp = new TrigProp { Rec = pr };
                for (int t = RI32(pr, 8); t <= RI32(pr, 0xC); t++) pp.Trigs.Add(Slice(oPT + t * 0x14, 0x14));
                ub.Props.Add(pp);
            }
            for (int t = 0; t < nAlw; t++) ub.Always.Add(Slice(oAlw + t * 0x10, 0x10));
        }

        // assign New to every node (roots first, then DFS block-allocating children); old->new map for surviving nodes
        List<CallNode> Layout(UserBlock ub, out Dictionary<int, int> o2n)
        {
            var order = new List<CallNode>(); int next = 0;
            foreach (var r in ub.Roots) { r.New = next++; order.Add(r); }
            var stack = new Stack<CallNode>();
            for (int i = ub.Roots.Count - 1; i >= 0; i--) stack.Push(ub.Roots[i]);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                foreach (var c in n.Children) { c.New = next++; order.Add(c); }
                for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
            }
            o2n = new Dictionary<int, int>();
            foreach (var n in order) if (n.Old >= 0) o2n[n.Old] = n.New;
            return order;
        }
        void RemapExistingTriggers(UserBlock ub, Dictionary<int, int> o2n)
        {
            foreach (var s in ub.Slots) foreach (var a in s.Actions) foreach (var tr in a.Trigs) RW32(tr, 4, (uint)(o2n[(int)(RU32(tr, 4) / 0x20)] * 0x20));
            foreach (var p in ub.Props) foreach (var tr in p.Trigs) RW32(tr, 4, (uint)(o2n[(int)(RU32(tr, 4) / 0x20)] * 0x20));
            foreach (var tr in ub.Always) RW32(tr, 4, (uint)(o2n[(int)(RU32(tr, 4) / 0x20)] * 0x20));
        }
        byte[] SerializeTriggers(UserBlock ub, out int[] counts)
        {
            var slotB = new List<byte>(); var actB = new List<byte>(); var atrB = new List<byte>(); int ai = 0, ti = 0;
            foreach (var s in ub.Slots)
            {
                int aS = ai;
                foreach (var act in s.Actions)
                {
                    int tS = ti;
                    foreach (var tr in act.Trigs) { atrB.AddRange(tr); ti++; }
                    var ar = (byte[])act.Rec.Clone(); RW32(ar, 4, (uint)tS); RW32(ar, 8, (uint)(ti - 1)); actB.AddRange(ar); ai++;
                }
                var sr = (byte[])s.Rec.Clone(); RW16(sr, 4, (ushort)aS); RW16(sr, 6, (ushort)(ai - 1)); slotB.AddRange(sr);
            }
            var propB = new List<byte>(); var ptrB = new List<byte>(); int pti = 0;
            foreach (var p in ub.Props)
            {
                int tS = pti;
                foreach (var tr in p.Trigs) { ptrB.AddRange(tr); pti++; }
                var pr = (byte[])p.Rec.Clone(); RW32(pr, 8, (uint)tS); RW32(pr, 0xC, (uint)(pti - 1)); propB.AddRange(pr);
            }
            var alwB = new List<byte>(); foreach (var a in ub.Always) alwB.AddRange(a);
            counts = new[] { ub.Slots.Count, ai, ti, ub.Props.Count, pti, ub.Always.Count };
            var all = new List<byte>(slotB); all.AddRange(actB); all.AddRange(atrB); all.AddRange(propB); all.AddRange(ptrB); all.AddRange(alwB);
            return all.ToArray();
        }
        byte[] SerializeUser(UserBlock ub, List<CallNode> order)
        {
            int nCallNew = order.Count;
            var newActs = new List<byte[]>();
            int leafRank = 0;       // ACT+0x04 is the leaf's rank among the user's leaves (containers carry -1), regenerated here
            foreach (var n in order)
            {
                var a = (byte[])n.Act.Clone();
                RW32(a, 0xC, (uint)(n.Parent != null ? n.Parent.New : -1));        // parentIndex
                RW16(a, 4, n.IsContainer ? (ushort)0xFFFF : (ushort)leafRank);
                if (!n.IsContainer) leafRank++;
                newActs.Add(a);
            }
            var cont = new List<byte>(); var pocOf = new Dictionary<CallNode, int>();
            foreach (var n in order)
            {
                if (!n.IsContainer) continue;
                pocOf[n] = cont.Count; var rec = (byte[])n.Rec.Clone();
                if (n.Children.Count > 0) { RW32(rec, 4, (uint)n.Children[0].New); RW32(rec, 8, (uint)n.Children[n.Children.Count - 1].New); }
                else { RW32(rec, 4, 0); RW32(rec, 8, 0xFFFFFFFF); }
                cont.AddRange(rec);
            }
            for (int k = 0; k < order.Count; k++) if (order[k].IsContainer) RW32(newActs[k], 0x18, (uint)pocOf[order[k]]);
            var keyed = new List<CallNode>(order);
            keyed.Sort((x, y) => { int c = CompareBytes(NameBytes(RU32(x.Act, 0)), NameBytes(RU32(y.Act, 0))); return c != 0 ? c : x.New - y.New; });
            var sid = new byte[((nCallNew * 2) + 3) & ~3];
            for (int pos = 0; pos < keyed.Count; pos++) RW16(sid, pos * 2, (ushort)keyed[pos].New);
            byte[] trig = SerializeTriggers(ub, out int[] tc);
            var prefix = (byte[])ub.Prefix.Clone();
            RW32(prefix, 8, (uint)nCallNew);
            int nAsset = 0; foreach (var n in order) if (!n.IsContainer) nAsset++;
            RW32(prefix, 0xC, (uint)nAsset);
            RW32(prefix, 0x14, (uint)tc[0]); RW32(prefix, 0x18, (uint)tc[1]); RW32(prefix, 0x1C, (uint)tc[2]);
            RW32(prefix, 0x20, (uint)tc[3]); RW32(prefix, 0x24, (uint)tc[4]); RW32(prefix, 0x28, (uint)tc[5]);
            RW32(prefix, 0x2C, (uint)(0x30 + ub.NLocal * 4 + NumUserParams * 4 + sid.Length + nCallNew * 0x20 + cont.Count));
            var outb = new List<byte>(prefix);
            outb.AddRange(ub.Locals); outb.AddRange(ub.UParams); outb.AddRange(sid);
            foreach (var a in newActs) outb.AddRange(a);
            outb.AddRange(cont); outb.AddRange(trig);
            return outb.ToArray();
        }
        // Replace user `ui`'s block with `newblock`; shift every downstream offset (delta may be negative).
        void SpliceUser(int ui, byte[] newblock)
        {
            long b = UserOffsets[ui]; long oldsz = BlockSizeAt(b); int delta = newblock.Length - (int)oldsz;
            var nd = new byte[D.Length + delta];
            Array.Copy(D, 0, nd, 0, (int)b);
            Array.Copy(newblock, 0, nd, (int)b, newblock.Length);
            Array.Copy(D, (int)(b + oldsz), nd, (int)(b + newblock.Length), (int)(D.Length - (b + oldsz)));
            D = nd;
            WU32(4, (uint)((long)U32(4) + delta));                                  // FileSize
            foreach (int i in new[] { 5, 6, 13, 15, 16 }) { uint v = U32(4 + i * 4); if (v > b) WU32(4 + i * 4, (uint)((long)v + delta)); }
            long uoBase = 0x48 + NumUser * 4;
            for (int i = 0; i < NumUser; i++) { uint v = U32(uoBase + i * 4); if (v > b) WU32(uoBase + i * 4, (uint)((long)v + delta)); }
            Reindex(); _editInit = false;
        }

        void CollectSubtree(CallNode n, HashSet<CallNode> outset) { if (outset.Add(n)) foreach (var c in n.Children) CollectSubtree(c, outset); }
        CallNode CloneSubtree(CallNode n)
        {
            var c = new CallNode { Act = (byte[])n.Act.Clone(), IsContainer = n.IsContainer, Rec = n.Rec != null ? (byte[])n.Rec.Clone() : null, Old = -1 };
            foreach (var ch in n.Children) { var cc = CloneSubtree(ch); cc.Parent = c; c.Children.Add(cc); }
            return c;
        }
        string UniqueName(UserBlock ub, string candidate)
        {
            var names = new HashSet<string>(); foreach (var n in ub.All) names.Add(Name(RU32(n.Act, 0)));
            if (!names.Contains(candidate)) return candidate;
            for (int k = 2; ; k++) { string c = candidate + k; if (!names.Contains(c)) return c; }
        }
        void RemoveTriggersTargeting(UserBlock ub, HashSet<int> remOld)
        {
            foreach (var s in ub.Slots) { foreach (var a in s.Actions) a.Trigs.RemoveAll(tr => remOld.Contains((int)(RU32(tr, 4) / 0x20))); s.Actions.RemoveAll(a => a.Trigs.Count == 0); }
            ub.Slots.RemoveAll(s => s.Actions.Count == 0);
            foreach (var p in ub.Props) p.Trigs.RemoveAll(tr => remOld.Contains((int)(RU32(tr, 4) / 0x20)));
            ub.Props.RemoveAll(p => p.Trigs.Count == 0);
            ub.Always.RemoveAll(tr => remOld.Contains((int)(RU32(tr, 4) / 0x20)));
        }
        void CloneInList(List<byte[]> trigs, int origNew, int cloneNew)
        {
            for (int i = trigs.Count - 1; i >= 0; i--)
                if ((int)(RU32(trigs[i], 4) / 0x20) == origNew) { var cl = (byte[])trigs[i].Clone(); RW32(cl, 4, (uint)(cloneNew * 0x20)); trigs.Insert(i + 1, cl); }
        }
        void CloneTriggersForRoot(UserBlock ub, int origNew, int cloneNew)
        {
            foreach (var s in ub.Slots) foreach (var a in s.Actions) CloneInList(a.Trigs, origNew, cloneNew);
            foreach (var p in ub.Props) CloneInList(p.Trigs, origNew, cloneNew);
            CloneInList(ub.Always, origNew, cloneNew);
        }
        // Matrix (bit 9) + BitFlag (bit 28) refs from any canonical leaf block, reused verbatim for a fabricated leaf.
        uint[] TemplateLeafRefs()
        {
            for (int ui = 0; ui < NumUser; ui++)
            {
                long b = UserOffsets[ui]; int nLocal = (int)U32(b + 4), nCall = (int)U32(b + 8);
                long ACT = b + 0x30 + nLocal * 4 + NumUserParams * 4 + (((nCall * 2) + 3) & ~3);
                for (int i = 0; i < nCall; i++)
                {
                    long a = ACT + i * 0x20;
                    if ((U16(a + 6) & 1) == 0) { uint poc = U32(a + 0x18); long bs = AssetParamPos + poc; if (U64(bs) == 0x10000203UL) return new[] { U32(bs + 8 + 8), U32(bs + 8 + 12) }; }
                }
            }
            return new[] { 0u, 0x05000004u };   // canonical fallback: Matrix=Direct[0], BitFlag=Bitfield[4]
        }
        CallNode NewLeaf(uint nameOff, string name, uint blockOff)
        {
            var a = new byte[0x20];
            RW32(a, 0, nameOff); RW32(a, 8, 1); RW32(a, 0xC, 0xFFFFFFFF);
            RW32(a, 0x10, Crc32(name + "#g")); RW32(a, 0x14, Crc32(name)); RW32(a, 0x18, blockOff); RW32(a, 0x1C, 0xFFFFFFFF);
            return new CallNode { Act = a, IsContainer = false, Rec = null, Old = -1 };
        }
        CallNode NewContainer(uint nameOff, string name, int ctype)
        {
            var a = new byte[0x20];
            RW32(a, 0, nameOff); RW16(a, 6, 1); RW32(a, 8, 1); RW32(a, 0xC, 0xFFFFFFFF);
            RW32(a, 0x10, Crc32(name + "#g")); RW32(a, 0x14, Crc32(name)); RW32(a, 0x1C, 0xFFFFFFFF);
            var rec = new byte[0xC]; RW32(rec, 0, (uint)ctype);   // Blend/Random/Sequence record (child range filled on layout)
            return new CallNode { Act = a, IsContainer = true, Rec = rec, Old = -1 };
        }
        byte[] NewAlwaysTrigger(string name, int newIdx)
        {
            var t = new byte[0x10];
            RW32(t, 0, Crc32(name + "#trig")); RW32(t, 4, (uint)(newIdx * 0x20));
            // @8 and @0xC are undecoded; these are the values most of the file's unconditional alwaysTriggers carry.
            RW32(t, 8, 0x00004c8d); RW32(t, 0xC, 0xFFFFFFFF);
            return t;
        }

        // ---- public node-op entry points (return "" on success, else a short reason) ----
        public string DeleteNode(int ui, int ci)
        {
            var ub = ParseUser(ui);
            if (ci < 0 || ci >= ub.All.Count) return "no such node";
            var rem = new HashSet<CallNode>(); CollectSubtree(ub.All[ci], rem);
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (var n in ub.All)
                {
                    if (rem.Contains(n) || !n.IsContainer || n.Children.Count == 0) continue;
                    bool all = true; foreach (var c in n.Children) if (!rem.Contains(c)) { all = false; break; }
                    if (all) { rem.Add(n); changed = true; }
                }
            }
            if (rem.Count >= ub.All.Count) return "that would delete the whole effect list";
            var remOld = new HashSet<int>(); foreach (var n in rem) remOld.Add(n.Old);
            foreach (var n in ub.All) if (!rem.Contains(n)) n.Children.RemoveAll(c => rem.Contains(c));
            ub.Roots.RemoveAll(r => rem.Contains(r));
            RemoveTriggersTargeting(ub, remOld);
            var order = Layout(ub, out var o2n);
            RemapExistingTriggers(ub, o2n);
            SpliceUser(ui, SerializeUser(ub, order)); Dirty = true; return "";
        }
        // null if `parentCi` is a valid container to add a child under (or < 0 for top level); else a reason string.
        string CheckParent(int ui, int parentCi)
        {
            if (parentCi < 0) return null;
            if (parentCi >= (int)U32(UserOffsets[ui] + 8)) return "no such parent";
            return (U16(LocateACT(ui, parentCi) + 6) & 1) != 0 ? null : "parent is not a container";
        }
        // The node-op methods return "" on success (else a short reason) and set newIndex = the new node's call index.
        public string DuplicateNode(int ui, int ci, out int newIndex)
        {
            newIndex = -1;
            var ub = ParseUser(ui);
            if (ci < 0 || ci >= ub.All.Count) return "no such node";
            var orig = ub.All[ci];
            var clone = CloneSubtree(orig);
            string newName = UniqueName(ub, Name(RU32(orig.Act, 0)) + "_copy");
            uint nameOff = ResolveNameOffset(newName);          // grows the name pool (at EOF); the user block is untouched
            RW32(clone.Act, 0, nameOff); RW32(clone.Act, 0x14, Crc32(newName));
            bool topLevel = orig.Parent == null;
            if (topLevel) ub.Roots.Insert(ub.Roots.IndexOf(orig) + 1, clone);
            else { var p = orig.Parent; clone.Parent = p; p.Children.Insert(p.Children.IndexOf(orig) + 1, clone); }
            var order = Layout(ub, out var o2n);
            RemapExistingTriggers(ub, o2n);
            if (topLevel) CloneTriggersForRoot(ub, orig.New, clone.New);   // same event fires the copy too
            SpliceUser(ui, SerializeUser(ub, order)); Dirty = true;
            newIndex = clone.New; return "";
        }
        public string CreateLeaf(int ui, int parentCi, string callName, string emitterSet, out int newIndex)
        {
            newIndex = -1;
            string bad = CheckParent(ui, parentCi); if (bad != null) return bad;
            uint offCall = ResolveNameOffset(callName), offEset = ResolveNameOffset(emitterSet);
            uint[] tmpl = TemplateLeafRefs();
            uint blockOff = AppendBlock(0x10000203UL, new[] { (1u << 24) | offCall, (1u << 24) | offEset, tmpl[0], tmpl[1] });
            var ub = ParseUser(ui);                              // re-parse after the appends shifted this user's offset
            var leaf = NewLeaf(offCall, callName, blockOff);
            if (parentCi < 0) ub.Roots.Add(leaf);
            else { var p = ub.All[parentCi]; leaf.Parent = p; p.Children.Add(leaf); }
            var order = Layout(ub, out var o2n);
            RemapExistingTriggers(ub, o2n);
            if (parentCi < 0) ub.Always.Add(NewAlwaysTrigger(callName, leaf.New));
            SpliceUser(ui, SerializeUser(ub, order)); Dirty = true;
            newIndex = leaf.New; return "";
        }
        public string CreateContainer(int ui, int parentCi, int ctype, string contName, string childName, string emitterSet, out int newIndex)
        {
            newIndex = -1;
            string bad = CheckParent(ui, parentCi); if (bad != null) return bad;
            uint offChild = ResolveNameOffset(childName), offEset = ResolveNameOffset(emitterSet), offCont = ResolveNameOffset(contName);
            uint[] tmpl = TemplateLeafRefs();
            uint blockOff = AppendBlock(0x10000203UL, new[] { (1u << 24) | offChild, (1u << 24) | offEset, tmpl[0], tmpl[1] });
            var ub = ParseUser(ui);
            var leaf = NewLeaf(offChild, childName, blockOff);
            var cont = NewContainer(offCont, contName, ctype);
            cont.Children.Add(leaf); leaf.Parent = cont;
            if (parentCi < 0) ub.Roots.Add(cont);
            else { var p = ub.All[parentCi]; cont.Parent = p; p.Children.Add(cont); }
            var order = Layout(ub, out var o2n);
            RemapExistingTriggers(ub, o2n);
            if (parentCi < 0) ub.Always.Add(NewAlwaysTrigger(contName, cont.New));
            SpliceUser(ui, SerializeUser(ub, order)); Dirty = true;
            newIndex = cont.New; return "";
        }

        // =====================================================================================
        //  Actor (whole-user) operations: create / delete / duplicate a user entry. The
        //  UserDataTable is NumUser sorted CRC32 name-hashes followed by NumUser parallel exRegion
        //  offsets, and the blocks are stored in that hash order. Adding or removing an actor
        //  inserts/removes a (hash, offset) pair and the user's exRegion block; the ParamDefineTable
        //  (derived position) and every region after the header shift, so the whole file past the
        //  header is rebuilt and Reindex recomputes the table positions.

        // Reassemble: header(0x48) + UserDataTable(hashes then offsets) + content[oldPDT..spliceAt) +
        // insert + content[spliceAt+removeLen..]. oldPDT = 0x48 + NumUser*8 (the current ParamDefineTable).
        byte[] RebuildWithUDT(uint[] hashes, uint[] offsets, long oldPDT, long spliceAt, byte[] insert, long removeLen)
        {
            int udt = hashes.Length * 8, before = (int)(spliceAt - oldPDT), after = (int)(D.Length - (spliceAt + removeLen));
            var outb = new byte[0x48 + udt + before + (insert != null ? insert.Length : 0) + after];
            Array.Copy(D, 0, outb, 0, 0x48);
            int q = 0x48;
            foreach (uint h in hashes) { RW32(outb, q, h); q += 4; }
            foreach (uint o in offsets) { RW32(outb, q, o); q += 4; }
            Array.Copy(D, (int)oldPDT, outb, q, before); q += before;
            if (insert != null) { Array.Copy(insert, 0, outb, q, insert.Length); q += insert.Length; }
            Array.Copy(D, (int)(spliceAt + removeLen), outb, q, after);
            return outb;
        }
        void RememberName(uint hash, string name)   // so a freshly-added actor shows by name, not hash
        { if (NameMap == null) NameMap = new Dictionary<uint, string>(); NameMap[hash] = name; NamesDirty = true; }
        public bool NamesDirty;   // a custom actor name was added/renamed -> the names sidecar is persisted on save
        public IEnumerable<string> AllNames { get { return NameMap != null ? NameMap.Values : Enumerable.Empty<string>(); } }

        public string DeleteUser(int ui)
        {
            if (NumUser <= 1) return "cannot delete the last actor";
            long oldPDT = 0x48 + (long)NumUser * 8, B = UserOffsets[ui], S = BlockSizeAt(B);
            int n = (int)NumUser;
            var nh = new uint[n - 1]; var no = new uint[n - 1];
            for (int i = 0, j = 0; i < n; i++)
            {
                if (i == ui) continue;
                nh[j] = UserHashes[i];
                uint O = UserOffsets[i]; no[j] = (uint)((long)O - 8 - (O > B ? S : 0)); j++;
            }
            uint h5 = TrigPos, h6 = LocPropPos, h13 = ExRegionPos, h15 = CondPos, h16 = NameTablePos;
            D = RebuildWithUDT(nh, no, oldPDT, B, null, S);
            WU32(4, (uint)D.Length); WU32(0x3C, NumUser - 1);
            WU32(0x18, h5 - 8); WU32(0x1C, h6 - 8); WU32(0x38, h13 - 8);
            WU32(0x40, (uint)((long)h15 - 8 - S)); WU32(0x44, (uint)((long)h16 - 8 - S));
            Reindex(); _editInit = false; Dirty = true; return "";
        }
        // Insert a new actor (block = a clone for duplicate, or a fabricated empty user for create). newUserIndex
        // gets the inserted user's index (its sorted hash position).
        string InsertUser(string newName, byte[] block, out int newUserIndex)
        {
            newUserIndex = -1;
            if (string.IsNullOrWhiteSpace(newName)) return "enter an actor name";
            uint newHash = Crc32(newName);
            int n = (int)NumUser;
            for (int i = 0; i < n; i++) if (UserHashes[i] == newHash) return "an actor with that name (or same CRC32) already exists";
            int pos = 0; while (pos < n && UserHashes[pos] < newHash) pos++;   // keep the hash table sorted
            long oldPDT = 0x48 + (long)NumUser * 8;
            int S = block.Length;
            long blockInsertOff = pos < n ? UserOffsets[pos] : CondPos;         // clone goes just before the block now at pos
            var nh = new uint[n + 1]; var no = new uint[n + 1];
            for (int i = 0; i < n + 1; i++)
            {
                if (i == pos) { nh[i] = newHash; no[i] = (uint)(blockInsertOff + 8); continue; }
                int src = i < pos ? i : i - 1;
                nh[i] = UserHashes[src];
                uint O = UserOffsets[src]; no[i] = (uint)((long)O + 8 + (O >= blockInsertOff ? S : 0));
            }
            uint h5 = TrigPos, h6 = LocPropPos, h13 = ExRegionPos, h15 = CondPos, h16 = NameTablePos;
            D = RebuildWithUDT(nh, no, oldPDT, blockInsertOff, block, 0);
            WU32(4, (uint)D.Length); WU32(0x3C, NumUser + 1);
            WU32(0x18, h5 + 8); WU32(0x1C, h6 + 8); WU32(0x38, h13 + 8);
            WU32(0x40, (uint)((long)h15 + 8 + S)); WU32(0x44, (uint)((long)h16 + 8 + S));
            Reindex(); _editInit = false; Dirty = true; RememberName(newHash, newName);
            newUserIndex = pos; return "";
        }
        public string DuplicateUser(int srcUi, string newName, out int newUserIndex)
        {
            long B = UserOffsets[srcUi]; int S = (int)BlockSizeAt(B);
            var clone = new byte[S]; Array.Copy(D, (int)B, clone, 0, S);
            return InsertUser(newName, clone, out newUserIndex);
        }
        public string CreateUser(string newName, out int newUserIndex)
        {
            var block = new byte[0x34]; RW32(block, 0x2C, 0x34);   // empty user: triggerTablePos=0x34, all else 0 (leafType=0)
            return InsertUser(newName, block, out newUserIndex);
        }
        // Rename an actor: recompute its CRC32 and move its UserDataTable entry to the new sorted position. The block
        // is unchanged and stays put (the offset table still maps to it), so the file size and every region are unchanged.
        public string RenameUser(int ui, string newName, out int newUserIndex)
        {
            newUserIndex = ui;
            if (string.IsNullOrWhiteSpace(newName)) return "enter an actor name";
            uint newHash = Crc32(newName);
            if (newHash == UserHashes[ui]) { RememberName(newHash, newName); return ""; }   // same name -> just relabel
            int n = (int)NumUser;
            for (int i = 0; i < n; i++) if (i != ui && UserHashes[i] == newHash) return "an actor with that name (or same CRC32) already exists";
            var hs = (uint[])UserHashes.Clone(); var os = (uint[])UserOffsets.Clone();
            hs[ui] = newHash;
            var idx = new int[n]; for (int i = 0; i < n; i++) idx[i] = i;
            Array.Sort(idx, (a, b) => hs[a].CompareTo(hs[b]));   // keep the hash table sorted
            for (int i = 0; i < n; i++) { WU32(0x48 + i * 4, hs[idx[i]]); WU32(0x48 + (long)n * 4 + i * 4, os[idx[i]]); }
            Reindex(); RememberName(newHash, newName); Dirty = true;
            for (int i = 0; i < n; i++) if (UserHashes[i] == newHash) { newUserIndex = i; break; }
            return "";
        }

        // ---- decode one user ----
        public ELinkUserModel DecodeUser(int ui)
        {
            var m = new ELinkUserModel();
            long b = UserOffsets[ui];
            int nLocal = (int)U32(b + 4), nCall = (int)U32(b + 8);
            long p = b + 0x30;
            var locals = new List<string>();
            for (int i = 0; i < nLocal; i++) { locals.Add(Name(U32(p))); p += 4; }
            m.LocalProps = locals;
            _curLocals = locals;
            for (int i = 0; i < NumUserParams; i++) { m.UserParams.Add(new KeyValuePair<string, string>(UserParamNames[i], ResolveTyped(UserParamTypes[i], U32(p)))); p += 4; }
            p += ((nCall * 2) + 3) & ~3;        // skip sortedAssetIdTable
            long ACT = p;
            long containerTablePos = ACT + 0x20 * nCall;

            for (int i = 0; i < nCall; i++)
                if (I32(ACT + i * 0x20 + 0xC) == -1)   // parentIndex == -1 -> top-level call
                    m.Tops.Add(BuildCall(ui, ACT, containerTablePos, i, locals));

            DecodeTriggers(b, ACT, nCall, m.TriggerLines);
            _curLocals = null;
            return m;
        }

        ELinkCall BuildCall(int ui, long ACT, long containerTablePos, int i, List<string> locals)
        {
            long a = ACT + i * 0x20;
            var node = new ELinkCall { Name = Name(U32(a)), Guid = U32(a + 0x10), UserIndex = ui, CallIndex = i };
            ushort flags = U16(a + 6);
            uint paramOrCont = U32(a + 0x18), cond = U32(a + 0x1C);
            node.IsContainer = (flags & 1) != 0;
            node.Condition = DecodeCondition(cond);
            if (node.IsContainer)
            {
                long c = containerTablePos + paramOrCont;
                node.ContainerType = (int)U32(c);
                node.ContainerTypeName = node.ContainerType >= 0 && node.ContainerType < CT.Length ? CT[node.ContainerType] : node.ContainerType.ToString();
                int cs = I32(c + 4), ce = I32(c + 8);
                if (node.ContainerType == 0) { uint wp = U32(c + 0xC); node.Watch = wp != 0 ? Name(wp) : ""; }
                node.Children = new List<ELinkCall>();
                for (int ch = cs; ch <= ce; ch++)         // childEnd is INCLUSIVE in BotW v30
                    node.Children.Add(BuildCall(ui, ACT, containerTablePos, ch, locals));
            }
            else
            {
                node.ParamOffset = paramOrCont;
                node.Overrides = new List<ELinkOverride>();
                long bs = AssetParamPos + paramOrCont; ulong mask = U64(bs); long q = bs + 8;
                for (int bit = 0; bit < 64; bit++)
                {
                    if (((mask >> bit) & 1) == 0) continue;
                    uint reff = U32(q); q += 4;
                    if (bit >= AssetNames.Length) continue;
                    int rt = (int)(reff >> 24), t = AssetTypes[bit];
                    var ov = new ELinkOverride {
                        Name = AssetNames[bit], Bit = bit, Type = t, RefType = rt,
                        Display = ResolveTyped(t, reff), Scalar = ScalarOf(t, reff),
                        Editable = rt == 0 && (t == 0 || t == 1 || t == 2 || t == 3),   // Direct int/float/bool/enum
                    };
                    node.Overrides.Add(ov);
                    if (AssetNames[bit] == "RuntimeAssetName" && rt == 1) node.RuntimeAssetName = Name(reff & 0xFFFFFF);
                }
            }
            return node;
        }

        void DecodeTriggers(long b, long ACT, int nCall, List<string> outLines)
        {
            int nSlot = (int)U32(b + 0x14), nAction = (int)U32(b + 0x18), nAT = (int)U32(b + 0x1C);
            int nProp = (int)U32(b + 0x20), nPT = (int)U32(b + 0x24), nAlw = (int)U32(b + 0x28);
            if ((nSlot | nAction | nAT | nProp | nPT | nAlw) == 0) return;
            long tb = b + U32(b + 0x2C);
            long oSlots = tb, oAct = oSlots + nSlot * 8, oAtrg = oAct + nAction * 0xC;
            long oProp = oAtrg + nAT * 0x18, oPtrg = oProp + nProp * 0x10, oAlw = oPtrg + nPT * 0x14;
            Func<uint, string> asset = off => { int idx = (int)(off / 0x20); return (idx >= 0 && idx < nCall) ? Name(U32(ACT + idx * 0x20)) : "?idx" + idx; };
            for (int s = 0; s < nSlot; s++) { long sp = oSlots + s * 8; outLines.Add("ActionSlot \"" + Name(U32(sp)) + "\" -> actions[" + S16(sp + 4) + ".." + S16(sp + 6) + "]"); }
            for (int x = 0; x < nAction; x++) { long ap = oAct + x * 0xC; outLines.Add("Action \"" + Name(U32(ap)) + "\" -> triggers[" + I32(ap + 4) + ".." + I32(ap + 8) + "]"); }
            for (int t = 0; t < nAT; t++) { long tp = oAtrg + t * 0x18; outLines.Add("ActionTrigger asset=" + asset(U32(tp + 4)) + " frames[" + I32(tp + 8) + ".." + I32(tp + 0xC) + "] flags=0x" + U16(tp + 0x10).ToString("X")); }
            for (int pr = 0; pr < nProp; pr++) { long pp = oProp + pr * 0x10; outLines.Add("Property \"" + Name(U32(pp)) + "\" global=" + U32(pp + 4) + " -> triggers[" + I32(pp + 8) + ".." + I32(pp + 0xC) + "]"); }
            for (int t = 0; t < nPT; t++) { long tp = oPtrg + t * 0x14; uint co = U32(tp + 8); outLines.Add("PropertyTrigger asset=" + asset(U32(tp + 4)) + " cond=" + (co != 0xFFFFFFFF ? DecodeCondition(co) : "none")); }
            for (int t = 0; t < nAlw; t++) { long tp = oAlw + t * 0x10; outLines.Add("AlwaysTrigger asset=" + asset(U32(tp + 4)) + " flags=0x" + U16(tp + 8).ToString("X")); }
        }
    }

    // ---- tree nodes ----------------------------------------------------------------------------
    public class ELinkUserNode : TreeNodeCustom, IContextMenuNode
    {
        readonly ELinkFile elink; public readonly int Ui; bool built;
        public ELinkUserNode(ELinkFile e, int index)
        {
            elink = e; Ui = index;
            Text = e.UserDisplay(index);
            Nodes.Add(new TreeNode());   // placeholder so the expand arrow shows
        }
        public override void OnExpand() { if (built) return; built = true; BuildChildren(); }
        public void Rebuild() { built = true; BuildChildren(); Expand(); }   // after a structural edit: indices changed, so re-decode
        void BuildChildren()
        {
            Nodes.Clear();
            var m = elink.DecodeUser(Ui);
            if (m.LocalProps.Count > 0) Nodes.Add(new TreeNode("LocalProperties { " + string.Join(", ", m.LocalProps) + " }"));
            if (m.UserParams.Count > 0) Nodes.Add(new TreeNode("UserParams { " + string.Join(", ", m.UserParams.Select(kv => kv.Key + "=" + kv.Value)) + " }"));
            foreach (var c in m.Tops) Nodes.Add(ELinkCallNode.Make(elink, c));
            if (m.TriggerLines.Count > 0)
            {
                var tn = new TreeNode("Triggers");
                foreach (var line in m.TriggerLines) tn.Nodes.Add(new TreeNode(line));
                Nodes.Add(tn);
            }
        }

        public ToolStripItem[] GetContextMenuItems()
        {
            return new ToolStripItem[]
            {
                new ToolStripMenuItem("Add effect...", null, (s, e) => DoCreateLeaf(-1)),
                new ToolStripMenuItem("Add effect group (Blend)...", null, (s, e) => DoCreateContainer(-1)),
                new ToolStripSeparator(),
                new ToolStripMenuItem("Duplicate actor...", null, (s, e) => DoDuplicateActor()),
                new ToolStripMenuItem("Rename actor...", null, (s, e) => DoRenameActor()),
                new ToolStripMenuItem("Delete actor", null, (s, e) => DoDeleteActor()),
            };
        }
        void DoDuplicateActor()
        {
            var root = Parent as ELink2DB; if (root == null) return;
            string name = ELinkNameDialog.Ask("Duplicate actor", "New actor name");
            if (name == null) return;
            int nu; string err = elink.DuplicateUser(Ui, name, out nu);
            if (!string.IsNullOrEmpty(err)) { MessageBox.Show(err, "ELink"); return; }
            root.RebuildUsers(); root.SelectUser(nu);
        }
        void DoRenameActor()
        {
            var root = Parent as ELink2DB; if (root == null) return;
            string name = ELinkNameDialog.Ask("Rename actor", "New actor name");
            if (name == null) return;
            int nu; string err = elink.RenameUser(Ui, name, out nu);
            if (!string.IsNullOrEmpty(err)) { MessageBox.Show(err, "ELink"); return; }
            root.RebuildUsers(); root.SelectUser(nu);
        }
        void DoDeleteActor()
        {
            var root = Parent as ELink2DB; if (root == null) return;
            if (MessageBox.Show("Delete actor \"" + Text + "\" and all its effects?", "Delete actor",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            string err = elink.DeleteUser(Ui);
            if (!string.IsNullOrEmpty(err)) { MessageBox.Show(err, "ELink"); return; }
            root.RebuildUsers();
        }
        // parentCi < 0 = top level (a new effect, fired by a new alwaysTrigger); >= 0 nests under that container.
        public void DoCreateLeaf(int parentCi)
        {
            using (var dlg = new ELinkCreateDialog(false))
                if (dlg.ShowDialog() == DialogResult.OK) { int ci; Done(elink.CreateLeaf(Ui, parentCi, dlg.NodeName, dlg.EmitterSet, out ci), ci); }
        }
        public void DoCreateContainer(int parentCi)
        {
            using (var dlg = new ELinkCreateDialog(true))
                if (dlg.ShowDialog() == DialogResult.OK) { int ci; Done(elink.CreateContainer(Ui, parentCi, 3, dlg.NodeName, dlg.ChildName, dlg.EmitterSet, out ci), ci); }
        }
        public void DoDelete(ELinkCall call)
        {
            string what = call.IsContainer ? "\"" + call.Name + "\" and its contents" : "\"" + call.Name + "\"";
            if (MessageBox.Show("Delete " + what + "?\nAny trigger that fires it is removed too.", "Delete ELink node",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) Done(elink.DeleteNode(call.UserIndex, call.CallIndex));
        }
        public void DoDuplicate(ELinkCall call) { int ci; Done(elink.DuplicateNode(call.UserIndex, call.CallIndex, out ci), ci); }
        void Done(string err, int selectCi = -1)
        {
            if (!string.IsNullOrEmpty(err)) { MessageBox.Show(err, "ELink"); return; }
            Rebuild();
            if (selectCi >= 0) SelectCall(selectCi);   // highlight the just-created/duplicated node
        }
        void SelectCall(int ci)
        {
            var n = FindCall(Nodes, ci);
            if (n != null && TreeView != null) { TreeView.SelectedNode = n; n.EnsureVisible(); }
        }
        static ELinkCallNode FindCall(TreeNodeCollection nodes, int ci)
        {
            foreach (TreeNode n in nodes)
            {
                if (n is ELinkCallNode cn && cn.CallIndex == ci) return cn;
                var deep = FindCall(n.Nodes, ci);
                if (deep != null) return deep;
            }
            return null;
        }
    }

    // a call node is either a container folder (with child calls) or a clickable asset leaf.
    public class ELinkCallNode : TreeNodeCustom, IContextMenuNode
    {
        readonly ELinkFile elink; readonly ELinkCall call;
        ELinkCallNode(ELinkFile e, ELinkCall c) { elink = e; call = c; }
        public int CallIndex { get { return call.CallIndex; } }   // for re-selecting this node after a tree rebuild

        public static TreeNode Make(ELinkFile e, ELinkCall c)
        {
            var node = new ELinkCallNode(e, c);
            string label = c.Name + "  [0x" + c.Guid.ToString("X8") + "]";
            if (c.IsContainer)
                label += "  <" + c.ContainerTypeName + (string.IsNullOrEmpty(c.Watch) ? "" : ": " + c.Watch) + ">";
            if (c.Condition != null) label += "  (" + c.Condition + ")";
            node.Text = label;
            if (c.IsContainer && c.Children != null)
                foreach (var ch in c.Children) node.Nodes.Add(Make(e, ch));
            return node;
        }

        public override void OnClick(TreeView treeview)
        {
            if (call.IsContainer) return;     // folders have no editor panel
            var ed = (ELinkAssetEditor)LibraryGUI.GetActiveContent(typeof(ELinkAssetEditor));
            if (ed == null) { ed = new ELinkAssetEditor(); ed.Dock = DockStyle.Fill; LibraryGUI.LoadEditor(ed); }
            ed.Text = call.Name;
            ed.LoadAsset(elink, call);
        }

        public ToolStripItem[] GetContextMenuItems()
        {
            var owner = OwningUserNode();
            var items = new List<ToolStripItem>();
            if (owner == null) return items.ToArray();
            if (call.IsContainer)
            {
                items.Add(new ToolStripMenuItem("Add effect...", null, (s, e) => owner.DoCreateLeaf(call.CallIndex)));
                items.Add(new ToolStripMenuItem("Add effect group (Blend)...", null, (s, e) => owner.DoCreateContainer(call.CallIndex)));
                items.Add(new ToolStripSeparator());
            }
            items.Add(new ToolStripMenuItem("Duplicate", null, (s, e) => owner.DoDuplicate(call)));
            items.Add(new ToolStripMenuItem("Delete", null, (s, e) => owner.DoDelete(call)));
            return items.ToArray();
        }
        ELinkUserNode OwningUserNode()
        {
            TreeNode n = Parent;
            while (n != null && !(n is ELinkUserNode)) n = n.Parent;
            return n as ELinkUserNode;
        }
    }

    // Small dialog for creating a node: a call name (plus a child call name for a container) and the emitter set it
    // plays, chosen from the sets in any open .sesetlist. The set box is editable so it still works with none open.
    class ELinkCreateDialog : Form
    {
        readonly TextBox nameBox = new TextBox();
        readonly TextBox childBox;
        readonly ComboBox setBox = new ComboBox();
        readonly Button ok;
        public ELinkCreateDialog(bool container)
        {
            Text = container ? "Add effect group (Blend)" : "Add effect";
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            const int W = 380;
            ClientSize = new Size(W, container ? 214 : 166);

            var hint = new Label
            {
                Left = 12, Top = 10, Width = W - 24, AutoSize = false, Height = container ? 44 : 26, ForeColor = Color.Gray,
                Text = container ? "A Blend group plays several effects together. This creates the group and its first effect."
                                 : "Plays one emitter set.",
            };
            Controls.Add(hint);
            int y = hint.Bottom + 8;
            Controls.Add(new Label { Text = container ? "Group name" : "Effect name", Left = 12, Top = y + 3, Width = 110 });
            nameBox.SetBounds(126, y, W - 138, 23); Controls.Add(nameBox); y += 34;
            if (container)
            {
                childBox = new TextBox();
                Controls.Add(new Label { Text = "First effect name", Left = 12, Top = y + 3, Width = 110 });
                childBox.SetBounds(126, y, W - 138, 23); Controls.Add(childBox); y += 34;
            }
            Controls.Add(new Label { Text = "Emitter set", Left = 12, Top = y + 3, Width = 110 });
            setBox.SetBounds(126, y, W - 138, 23); setBox.DropDownStyle = ComboBoxStyle.DropDown;
            setBox.Items.AddRange(PTCL.GetEmitterSetNames().ToArray()); Controls.Add(setBox); y += 42;

            ok = new Button { Text = "OK", DialogResult = DialogResult.OK }; ok.SetBounds(W - 178, y, 80, 26);
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel }; cancel.SetBounds(W - 90, y, 80, 26);
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
            EventHandler v = (s, e) => ok.Enabled = NodeName.Length > 0 && EmitterSet.Length > 0 && (childBox == null || ChildName.Length > 0);
            nameBox.TextChanged += v; setBox.TextChanged += v; if (childBox != null) childBox.TextChanged += v;
            v(null, null);
        }
        public string NodeName { get { return nameBox.Text.Trim(); } }
        public string ChildName { get { return childBox != null ? childBox.Text.Trim() : ""; } }
        public string EmitterSet { get { return setBox.Text.Trim(); } }
    }

    // A one-field name prompt for adding or duplicating an actor; returns null on cancel.
    class ELinkNameDialog : Form
    {
        readonly TextBox box = new TextBox();
        ELinkNameDialog(string title, string label)
        {
            Text = title; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(324, 104);
            Controls.Add(new Label { Text = label, Left = 12, Top = 16, Width = 92 });
            box.SetBounds(110, 13, 200, 23); Controls.Add(box);
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK }; ok.SetBounds(142, 60, 80, 26);
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel }; cancel.SetBounds(230, 60, 80, 26);
            Controls.Add(ok); Controls.Add(cancel); AcceptButton = ok; CancelButton = cancel;
            EventHandler v = (s, e) => ok.Enabled = box.Text.Trim().Length > 0; box.TextChanged += v; v(null, null);
        }
        public static string Ask(string title, string label)
        {
            using (var d = new ELinkNameDialog(title, label))
                return d.ShowDialog() == DialogResult.OK ? d.box.Text.Trim() : null;
        }
    }

    // ---- editor panel --------------------------------------------------------------------------
    // Left: the asset's overrides (text). Right: a live preview of the emitter set rendered WITH those overrides.
    // The preview is a single reused GL host (this editor is a GetActiveContent singleton); the old render is
    // QueueDispose'd before the next, mirroring EmitterEditorNX, so it adds no GL-leak surface.
    public class ELinkAssetEditor : STUserControl
    {
        readonly PropertyGrid grid;
        readonly STLabel header;
        readonly STLabel hint;
        readonly LinkLabel link;
        readonly STPanel previewHost;
        readonly STLabel previewMsg;
        Viewport previewViewport; DrawableContainer previewContainer; EftEmitterRender previewRender;
        bool previewFailed;
        ELinkFile elink; ELinkCall currentCall; ELinkParamBag bag;

        public ELinkAssetEditor()
        {
            header = new STLabel { Dock = DockStyle.Top, AutoSize = false, Height = 58, Padding = new Padding(6, 6, 6, 0), ForeColor = Color.Gainsboro };
            link = new LinkLabel
            {
                Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 0, 6, 2),
                BackColor = Color.FromArgb(40, 40, 40), LinkColor = Color.FromArgb(86, 156, 214),
                ActiveLinkColor = Color.FromArgb(120, 180, 235), Text = "Open original emitter set", Visible = false,
            };
            link.LinkClicked += OnOpenOriginal;
            // every param is listed; a blank value = no override. Type a value to add/set it; clear it to remove it.
            hint = new STLabel
            {
                Dock = DockStyle.Top, AutoSize = false, Height = 30, Padding = new Padding(6, 4, 6, 4), ForeColor = Color.Gray,
                Text = "Blank = not overridden. Type a value to add/edit; clear it (or right-click > Reset) to remove.",
            };

            grid = new PropertyGrid
            {
                Dock = DockStyle.Fill, ToolbarVisible = false, HelpVisible = true, PropertySort = PropertySort.NoSort,
                ViewBackColor = Color.FromArgb(40, 40, 40), ViewForeColor = Color.Gainsboro, LineColor = Color.FromArgb(60, 60, 60),
                CategoryForeColor = Color.Gainsboro, HelpBackColor = Color.FromArgb(45, 45, 45), HelpForeColor = Color.Gainsboro,
                BackColor = Color.FromArgb(40, 40, 40),
            };
            grid.SelectedGridItemChanged += OnGridSelected;   // click an expandable (Float) row -> open it so the Mode/Min/Max show

            previewHost = new STPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(40, 40, 40) };
            // shown when the referenced .sesetlist is not open (so the blank pane is explained, not mistaken for broken)
            previewMsg = new STLabel { Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleCenter, ForeColor = Color.Gray };
            previewHost.Controls.Add(previewMsg);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.Panel1,
                SplitterWidth = 5, Panel1MinSize = 260,
            };
            split.Panel1.Controls.Add(grid);     // Fill added first; the Top bars stack above it (header topmost = added last)
            split.Panel1.Controls.Add(hint);
            split.Panel1.Controls.Add(link);
            split.Panel1.Controls.Add(header);
            split.Panel2.Controls.Add(previewHost);
            Controls.Add(split);
            HandleCreated += (s, e) => { try { split.SplitterDistance = Math.Min(420, Math.Max(280, Width / 2)); } catch { } };
        }

        public void LoadAsset(ELinkFile file, ELinkCall c)
        {
            elink = file; currentCall = c;
            link.Visible = c.RuntimeAssetName != null;   // containers have no emitter set to open
            header.Text = c.Name + "   [0x" + c.Guid.ToString("X8") + "]"
                + (c.RuntimeAssetName != null ? "\r\nPlays emitter set : " + c.RuntimeAssetName : "")
                + (c.Condition != null ? "\r\nGated by : " + c.Condition : "");
            if (c.CallIndex < 0) { bag = null; grid.SelectedObject = null; RefreshPreview(); return; }
            bag = new ELinkParamBag(elink.ReadAllParams(c.UserIndex, c.CallIndex), CommitParam, CommitFloat);
            currentCall.Overrides = elink.ReadOverrides(c.UserIndex, c.CallIndex);
            grid.SelectedObject = bag;
            RefreshPreview();
        }

        // After an edit, refresh the SAME grid in place. Rebuilding SelectedObject instead tears down the active cell
        // editor mid-interaction (the Mode dropdown stops opening), so we update the model and call Refresh().
        void ReloadFromBuffer()
        {
            if (elink == null || currentCall == null || currentCall.CallIndex < 0 || bag == null) return;
            string keep = ExpandedFloatLabel(grid.SelectedGridItem);
            bag.Reload(elink.ReadAllParams(currentCall.UserIndex, currentCall.CallIndex));
            currentCall.Overrides = elink.ReadOverrides(currentCall.UserIndex, currentCall.CallIndex);   // set ones -> preview
            grid.Refresh();
            if (keep != null) ReExpand(keep);     // keep the edited Float row open across the refresh
            RefreshPreview();
        }

        // Commit a simple (string/int/bool/enum) param: blank removes, filled adds/edits.
        string CommitParam(ELinkParamRow row, string text)
        {
            string err = elink.SetParam(currentCall.UserIndex, currentCall.CallIndex, row.Bit, row.Type, text);
            if (err.Length == 0) BeginInvoke((Action)ReloadFromBuffer);   // defer refresh until SetValue returns
            return err;
        }
        // Commit a float param: mode 0 = not set, 1 = constant value, 2 = random min/max.
        string CommitFloat(ELinkParamRow row, int mode, double value, double min, double max)
        {
            string err = elink.SetFloatParam(currentCall.UserIndex, currentCall.CallIndex, row.Bit, mode, value, min, max);
            if (err.Length == 0) BeginInvoke((Action)ReloadFromBuffer);
            return err;
        }

        // selecting a Float row opens it (so a "(not set)" param immediately reveals Mode / Value / Min / Max)
        void OnGridSelected(object s, SelectedGridItemChangedEventArgs e)
        {
            var it = e.NewSelection;
            if (it != null && it.Expandable && !it.Expanded) { try { it.Expanded = true; } catch { } }
        }
        static string ExpandedFloatLabel(GridItem gi)
        {
            if (gi == null) return null;
            while (gi.Parent != null && gi.Parent.GridItemType == GridItemType.Property) gi = gi.Parent;
            return gi.GridItemType == GridItemType.Property ? gi.Label : null;
        }
        void ReExpand(string label)
        {
            var n = grid.SelectedGridItem; if (n == null) return;
            while (n.Parent != null) n = n.Parent;          // climb to root
            var item = FindItem(n, label);
            if (item != null) { try { item.Expanded = true; } catch { } }
        }
        static GridItem FindItem(GridItem node, string label)
        {
            foreach (GridItem c in node.GridItems)
            {
                if (c.GridItemType == GridItemType.Property && c.Label == label) return c;
                var r = FindItem(c, label); if (r != null) return r;
            }
            return null;
        }

        // Render the asset's emitter set with the ELink overrides applied, into the right pane. Single reused host:
        // tear down the previous render (QueueDispose) before adding the new one, exactly like EmitterEditorNX.
        void RefreshPreview()
        {
            if (previewFailed || previewHost == null) return;
            try
            {
                if (!(Runtime.UseOpenGL && !Runtime.UseLegacyGL)) return;
                if (previewRender != null && previewViewport != null) { previewViewport.RemoveDrawable(previewRender); previewRender.QueueDispose(); previewRender = null; }

                string set = currentCall != null ? currentCall.RuntimeAssetName : null;
                var emitters = set != null ? PTCL.GetEmitterSetEmitters(set) : new List<PTCL.Emitter>();
                var inputs = new List<EftEmitterRender.EmitterInput>();
                foreach (var em in emitters) { var inp = EftEmitterRender.BuildInput(em, "emitter"); if (inp != null && inp.Data != null) inputs.Add(inp); }

                if (previewContainer != null) previewContainer.Drawables.Clear();
                if (inputs.Count == 0)   // set not open (or no drawable emitters): show the hint, not a stale/blank render
                {
                    if (previewViewport != null) { previewViewport.ReloadDrawables(previewContainer); previewViewport.Visible = false; }
                    previewMsg.Text = set == null ? "" : "Open \"" + set + "\" (.sesetlist) to preview";
                    previewMsg.Visible = true;
                    return;
                }
                var render = new EftEmitterRender(inputs, BuildOverride(currentCall)) { AutoFrame = true };
                if (previewViewport == null)
                {
                    previewContainer = new DrawableContainer { Name = "elink" };
                    previewViewport = new Viewport(new List<DrawableContainer> { previewContainer }) { Dock = DockStyle.Fill };
                    previewHost.Controls.Add(previewViewport);
                }
                previewMsg.Visible = false;
                previewViewport.Visible = true;
                previewContainer.Drawables.Clear();
                previewContainer.Drawables.Add(render);
                previewRender = render;
                previewViewport.ReloadDrawables(previewContainer);
            }
            catch { previewFailed = true; }   // any GL/setup failure -> stop trying; the data pane is unaffected
        }

        // Map the asset's representative override values onto the renderer's multiplier struct. All ELink params here
        // are multipliers (ParamDefine defaults are 1.0); unrecognised params (Position/Rotation/etc.) are ignored.
        static EftEmitterRender.EftOverride BuildOverride(ELinkCall c)
        {
            var o = new EftEmitterRender.EftOverride();
            if (c == null || c.Overrides == null) return o;
            foreach (var ov in c.Overrides)
            {
                if (double.IsNaN(ov.Scalar)) continue;
                float v = (float)ov.Scalar;
                switch (ov.Name)
                {
                    case "Scale": o.ScaleMul = v; o.HasAny = true; break;
                    case "Duration": o.DurationFrames = (int)Math.Round(ov.Scalar); o.HasAny = true; break;   // frames; bounds the emission window
                    case "LifeScale": o.LifeMul = v; o.HasAny = true; break;
                    case "DirectionalVel": o.DirVelMul = v; o.HasAny = true; break;
                    case "EmissionRate": o.EmitRateMul = v; o.HasAny = true; break;
                    case "EmissionInterval": o.EmitIntervalMul = v; o.HasAny = true; break;
                    case "EmissionScale": o.EmitVolMul = v; o.HasAny = true; break;   // best-guess: scales the emission region (VolScale)
                    case "Alpha": o.AlphaMul = v; o.HasAny = true; break;
                    case "Red": o.RgbMul = new Vector3(v, o.RgbMul.Y, o.RgbMul.Z); o.HasAny = true; break;
                    case "Green": o.RgbMul = new Vector3(o.RgbMul.X, v, o.RgbMul.Z); o.HasAny = true; break;
                    case "Blue": o.RgbMul = new Vector3(o.RgbMul.X, o.RgbMul.Y, v); o.HasAny = true; break;
                }
            }
            return o;
        }

        void OnOpenOriginal(object sender, LinkLabelLinkClickedEventArgs e)
        {
            string name = currentCall != null ? currentCall.RuntimeAssetName : null;
            if (name == null) return;
            if (!PTCL.TrySelectEmitterSet(name))
                MessageBox.Show(Runtime.MainForm,
                    "Emitter set \"" + name + "\" is not in any open .sesetlist.\n" +
                    "Open the .sesetlist that contains it, then try again.",
                    "Open original emitter");
        }
    }

    // ---- dynamic PropertyGrid binding ----------------------------------------------------------
    // Exposes EVERY asset param as a string-valued PropertyGrid row (blank = not overridden). Values are stored as
    // clean text; the param's underlying type drives parsing on commit. A blank commit removes the override.
    public class ELinkParamBag : ICustomTypeDescriptor
    {
        readonly List<ELinkParamRow> _rows;
        readonly Func<ELinkParamRow, string, string> _commit;
        readonly Func<ELinkParamRow, int, double, double, double, string> _commitFloat;
        public ELinkParamBag(List<ELinkParamRow> rows, Func<ELinkParamRow, string, string> commit, Func<ELinkParamRow, int, double, double, double, string> commitFloat)
        { _rows = rows; _commit = commit; _commitFloat = commitFloat; }
        public void Reload(List<ELinkParamRow> rows) { _rows.Clear(); _rows.AddRange(rows); }   // refresh in place (no new SelectedObject)
        public PropertyDescriptorCollection GetProperties(Attribute[] attributes) { return GetProperties(); }
        public PropertyDescriptorCollection GetProperties()
        {
            var arr = new PropertyDescriptor[_rows.Count];
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                arr[i] = (r.Type == 1 && r.Mode != 3)                    // Float (not a curve) -> expandable Constant/Random
                    ? (PropertyDescriptor)new ELinkFloatDescriptor(new ELinkFloatBox(r, _commitFloat))
                    : new ELinkParamDescriptor(r, _commit);
            }
            return new PropertyDescriptorCollection(arr);
        }
        public object GetPropertyOwner(PropertyDescriptor pd) { return this; }
        public AttributeCollection GetAttributes() { return AttributeCollection.Empty; }
        public string GetClassName() { return "ELink overrides"; }
        public string GetComponentName() { return null; }
        public TypeConverter GetConverter() { return null; }
        public EventDescriptor GetDefaultEvent() { return null; }
        public PropertyDescriptor GetDefaultProperty() { return null; }
        public object GetEditor(Type editorBaseType) { return null; }
        public EventDescriptorCollection GetEvents() { return EventDescriptorCollection.Empty; }
        public EventDescriptorCollection GetEvents(Attribute[] attributes) { return EventDescriptorCollection.Empty; }
    }

    public class ELinkParamDescriptor : PropertyDescriptor
    {
        public readonly ELinkParamRow Row;
        readonly Func<ELinkParamRow, string, string> _commit;
        static readonly string[] TN = { "Int", "Float", "Bool", "Enum", "String", "Custom" };
        public ELinkParamDescriptor(ELinkParamRow row, Func<ELinkParamRow, string, string> commit)
            : base(row.Name, new Attribute[] { new CategoryAttribute("Overrides") }) { Row = row; _commit = commit; }
        public override Type ComponentType { get { return typeof(ELinkParamBag); } }
        public override bool IsReadOnly { get { return !Row.Editable; } }
        public override Type PropertyType { get { return typeof(string); } }     // string text -> blank means "unset"
        public override object GetValue(object component) { return Row.Text; }
        public override void SetValue(object component, object value)
        {
            string err = _commit(Row, Convert.ToString(value));
            if (!string.IsNullOrEmpty(err)) throw new Exception(err);            // PropertyGrid reverts + surfaces it
        }
        public override bool CanResetValue(object component) { return Row.IsSet; }   // Reset = remove the override
        public override void ResetValue(object component) { _commit(Row, ""); }
        public override bool ShouldSerializeValue(object component) { return Row.IsSet; }   // bold = overridden
        public override string Description
        {
            get
            {
                string t = Row.Type >= 0 && Row.Type < TN.Length ? TN[Row.Type] : Row.Type.ToString();
                if (!string.IsNullOrEmpty(Row.Kind)) t += " (" + Row.Kind + ")";
                if (!Row.Editable) t += " read-only";
                return t + ".  Blank = not overridden.";
            }
        }
    }

    // ---- expandable Float param (Constant value OR Random min/max) ------------------------------
    public enum FloatMode { NotSet, Constant, Random }

    [TypeConverter(typeof(ELinkFloatConverter))]
    public class ELinkFloatBox
    {
        public readonly ELinkParamRow Row;
        readonly Func<ELinkParamRow, int, double, double, double, string> _commit;
        public int Mode; public double Value, Min, Max;
        public ELinkFloatBox(ELinkParamRow r, Func<ELinkParamRow, int, double, double, double, string> commit)
        {
            Row = r; _commit = commit;
            Mode = (r.Mode >= 0 && r.Mode <= 2) ? r.Mode : 0;
            if (r.Mode == 1) { Value = r.Value; Min = Max = r.Value; }
            else if (r.Mode == 2) { Min = r.Min; Max = r.Max; Value = r.Min; }
            else { Value = 0; Min = 0; Max = 1; }
        }
        public string Apply() { return _commit(Row, Mode, Value, Min, Max); }   // commit current state to the file
        public override string ToString()
        {
            if (Mode == 0) return "(not set)";
            if (Mode == 2) return Fmt(Min) + " .. " + Fmt(Max);
            return Fmt(Value);
        }
        static string Fmt(double d) { return ((float)d).ToString(); }
    }

    // Shows Mode plus the fields relevant to that mode (Value, or Min+Max); the parent cell shows the summary.
    public class ELinkFloatConverter : ExpandableObjectConverter
    {
        public override bool GetPropertiesSupported(ITypeDescriptorContext c) { return true; }
        public override PropertyDescriptorCollection GetProperties(ITypeDescriptorContext c, object value, Attribute[] a)
        {
            var box = (ELinkFloatBox)value;
            var list = new List<PropertyDescriptor> { new ModeDesc(box) };
            if (box.Mode == 1) list.Add(new NumDesc(box, "Value"));
            else if (box.Mode == 2) { list.Add(new NumDesc(box, "Min")); list.Add(new NumDesc(box, "Max")); }
            return new PropertyDescriptorCollection(list.ToArray());
        }
        public override bool CanConvertTo(ITypeDescriptorContext c, Type t) { return t == typeof(string) || base.CanConvertTo(c, t); }
        public override object ConvertTo(ITypeDescriptorContext c, System.Globalization.CultureInfo ci, object value, Type t)
        { return t == typeof(string) ? value.ToString() : base.ConvertTo(c, ci, value, t); }

        class ModeDesc : PropertyDescriptor
        {
            readonly ELinkFloatBox box;
            // RefreshProperties.All: changing Mode changes which sub-fields exist (Value vs Min/Max), so re-query them.
            public ModeDesc(ELinkFloatBox b) : base("Mode", new Attribute[] { new RefreshPropertiesAttribute(RefreshProperties.All) }) { box = b; }
            public override Type ComponentType { get { return typeof(ELinkFloatBox); } }
            public override Type PropertyType { get { return typeof(FloatMode); } }
            public override bool IsReadOnly { get { return false; } }
            public override object GetValue(object c) { return (FloatMode)box.Mode; }
            public override void SetValue(object c, object v)
            {
                int nm = (int)(FloatMode)v; if (nm == box.Mode) return;
                if (nm == 2 && box.Mode != 2) { box.Min = box.Value; box.Max = box.Value; }   // seed range from the constant
                else if (nm == 1 && box.Mode == 2) box.Value = box.Min;                        // seed constant from min
                box.Mode = nm;
                string err = box.Apply(); if (!string.IsNullOrEmpty(err)) throw new Exception(err);
            }
            public override bool CanResetValue(object c) { return false; }
            public override void ResetValue(object c) { }
            public override bool ShouldSerializeValue(object c) { return false; }
            public override string Description { get { return "Constant value or Random range"; } }
        }
        class NumDesc : PropertyDescriptor
        {
            readonly ELinkFloatBox box; readonly string which;
            public NumDesc(ELinkFloatBox b, string w) : base(w, null) { box = b; which = w; }
            public override Type ComponentType { get { return typeof(ELinkFloatBox); } }
            public override Type PropertyType { get { return typeof(float); } }
            public override bool IsReadOnly { get { return false; } }
            public override object GetValue(object c) { return (float)(which == "Value" ? box.Value : which == "Min" ? box.Min : box.Max); }
            public override void SetValue(object c, object v)
            {
                double d = Convert.ToDouble(v);
                if (which == "Value") box.Value = d; else if (which == "Min") box.Min = d; else box.Max = d;
                string err = box.Apply(); if (!string.IsNullOrEmpty(err)) throw new Exception(err);
            }
            public override bool CanResetValue(object c) { return false; }
            public override void ResetValue(object c) { }
            public override bool ShouldSerializeValue(object c) { return false; }
        }
    }

    public class ELinkFloatDescriptor : PropertyDescriptor
    {
        readonly ELinkFloatBox box;
        public ELinkFloatDescriptor(ELinkFloatBox b) : base(b.Row.Name, new Attribute[] { new CategoryAttribute("Overrides") }) { box = b; }
        public override Type ComponentType { get { return typeof(ELinkParamBag); } }
        public override Type PropertyType { get { return typeof(ELinkFloatBox); } }
        public override bool IsReadOnly { get { return true; } }       // expand to edit Mode / Value / Min / Max
        public override object GetValue(object c) { return box; }
        public override void SetValue(object c, object v) { }
        public override bool CanResetValue(object c) { return box.Row.IsSet; }      // Reset = remove override
        public override void ResetValue(object c) { box.Mode = 0; box.Apply(); }
        public override bool ShouldSerializeValue(object c) { return box.Row.IsSet; }   // bold when overridden
        public override string Description { get { return "Float. Expand to set a Constant value or a Random min/max; Mode = NotSet removes it."; } }
    }
}
