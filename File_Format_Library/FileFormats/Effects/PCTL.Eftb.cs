using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using Toolbox.Library.IO;
using Toolbox.Library;
using Toolbox.Library.Rendering;
using OpenTK;
using System.IO;
using Syroot.BinaryData;
using System.Windows.Forms;
using Toolbox.Library.Forms;
using Bfres.Structs;

namespace FirstPlugin
{
    // EFTB (BotW .sesetlist) section/resource types split out of PCTL.cs: the section-tree editing node, the
    // texture + texture-descriptor types, and the PRIM mesh. PCTL.cs holds the parser and the shared types.
    public partial class PTCL
    {
        public class SectionBase : TreeNodeCustom, IContextMenuNode
        {
            public long Position;  //Offsets are relative to this
            public string Signature;
            public uint SectionSize;
            public uint SubSectionSize;
            public uint SubSectionOffset;
            public uint NextSectionOffset;
            public uint Unkown; //0xFFFFFFFF
            public uint BinaryDataOffset; //32
            public uint Unkown3; //0
            public uint SubSectionCount;

            public object BinaryData;

            //Right-click actions for emitter-set editing. EMTR/ESET/ESTA/TEXA/PRMA get edit menus (each splices a
            //whole section in/out and re-parses the file in place); every OTHER section signature intentionally
            //returns an empty (non-null) array, so it shows only the tree's generic Collapse/Expand items.
            public ToolStripItem[] GetContextMenuItems()
            {
                var items = new List<ToolStripItem>();
                if (Signature == "EMTR")
                {
                    items.Add(new ToolStripMenuItem("Duplicate Emitter", null, (s, e) => EmitterOp(EditOp.DuplicateEmitter)));
                    items.Add(new ToolStripMenuItem("Rename Emitter", null, (s, e) => EmitterOp(EditOp.RenameEmitter)));
                    items.Add(new ToolStripMenuItem("Delete Emitter", null, (s, e) => EmitterOp(EditOp.DeleteEmitter)));
                    items.Add(new ToolStripMenuItem("Export Emitter...", null, (s, e) => ExportEmitterDialog()));
                    items.Add(new ToolStripMenuItem("View Shader (GLSL)...", null, (s, e) => { var p = FindPtcl(); if (p != null) p.ShowEmitterShader(BinaryData as Emitter); }));
                }
                else if (Signature == "ESET")
                {
                    items.Add(new ToolStripMenuItem("Add Emitter", null, (s, e) => EmitterOp(EditOp.AddEmitter)));
                    items.Add(new ToolStripMenuItem("Import Emitter...", null, (s, e) => ImportEmitterDialog()));
                    items.Add(new ToolStripMenuItem("Duplicate Set", null, (s, e) => EmitterOp(EditOp.DuplicateSet)));
                    items.Add(new ToolStripMenuItem("Rename Set", null, (s, e) => EmitterOp(EditOp.RenameSet)));
                    items.Add(new ToolStripMenuItem("Delete Set", null, (s, e) => EmitterOp(EditOp.DeleteSet)));
                    items.Add(new ToolStripMenuItem("Export Set...", null, (s, e) => ExportSetDialog()));
                }
                else if (Signature == "ESTA")
                {
                    items.Add(new ToolStripMenuItem("Add Emitter Set", null, (s, e) => EmitterOp(EditOp.AddSet)));
                    items.Add(new ToolStripMenuItem("Import Set...", null, (s, e) => ImportSetDialog()));
                    items.Add(new ToolStripMenuItem("Clear All Emitter Sets", null, (s, e) => { var p = FindPtcl(); if (p != null) p.ClearAllSets(); }));
                }
                else if (Signature == "TEXA")
                {
                    items.Add(new ToolStripMenuItem("Create Texture", null, (s, e) => CreateTexture()));
                    items.Add(new ToolStripMenuItem("Clear All Textures", null, (s, e) => { var p = FindPtcl(); if (p != null) p.ClearAllTextures(); }));
                }
                else if (Signature == "PRMA")
                {
                    items.Add(new ToolStripMenuItem("Add Primitive", null, (s, e) => AddPrimitive()));
                    items.Add(new ToolStripMenuItem("Clear All Primitives", null, (s, e) => { var p = FindPtcl(); if (p != null) p.ClearAllPrimitives(); }));
                }
                else if (Signature == "SHDB")   //the "GTX Shader" node = this file's whole GX2 bundle
                {
                    items.Add(new ToolStripMenuItem("Prune Unused Shaders", null, (s, e) => { var p = FindPtcl(); if (p != null) p.PruneUnusedShaders(); }));
                    items.Add(new ToolStripMenuItem("Clear All Shaders", null, (s, e) => { var p = FindPtcl(); if (p != null) p.ClearAllShaders(); }));
                }
                return items.ToArray();
            }

            private PTCL FindPtcl()
            {
                System.Windows.Forms.TreeNode n = Parent;
                while (n != null && !(n is PTCL)) n = n.Parent;
                return n as PTCL;
            }

            //Export this EMTR (with its in-file textures/primitive) to a toolbox .eftemitter bundle.
            private void ExportEmitterDialog()
            {
                var ptcl = FindPtcl(); if (ptcl == null) return;
                var eset = Parent as SectionBase; var esta = (eset != null) ? eset.Parent as SectionBase : null;
                if (eset == null || esta == null) return;
                int setIndex = esta.ChildSections.IndexOf(eset);
                int emtrIndex = eset.ChildSections.IndexOf(this);
                var sfd = new SaveFileDialog();
                sfd.Filter = "Toolbox Emitter (*.eftemitter)|*.eftemitter|All files (*.*)|*.*";
                sfd.FileName = (Text ?? "emitter") + ".eftemitter";
                if (sfd.ShowDialog(Runtime.MainForm) == DialogResult.OK)
                    ptcl.ExportEmitter(setIndex, emtrIndex, BinaryData as Emitter, sfd.FileName);
            }
            //Import a .eftemitter bundle into THIS set (adds any missing bundled textures/primitive first).
            private void ImportEmitterDialog()
            {
                var ptcl = FindPtcl(); if (ptcl == null) return;
                var esta = Parent as SectionBase; if (esta == null) return;
                int setIndex = esta.ChildSections.IndexOf(this);
                var ofd = new OpenFileDialog();
                ofd.Filter = "Toolbox Emitter (*.eftemitter)|*.eftemitter|All files (*.*)|*.*";
                if (ofd.ShowDialog(Runtime.MainForm) == DialogResult.OK)
                    ptcl.ImportEmitter(setIndex, ofd.FileName);
            }
            //Export this whole ESET (its emitters + their in-file resources) to a .eftset bundle.
            private void ExportSetDialog()
            {
                var ptcl = FindPtcl(); if (ptcl == null) return;
                var esta = Parent as SectionBase; if (esta == null) return;
                int setIndex = esta.ChildSections.IndexOf(this);
                var ems = new List<Emitter>();
                foreach (var c in ChildSections) if (c.BinaryData is Emitter e) ems.Add(e);
                var sfd = new SaveFileDialog();
                sfd.Filter = "Toolbox Emitter Set (*.eftset)|*.eftset|All files (*.*)|*.*";
                sfd.FileName = (Text ?? "set") + ".eftset";
                if (sfd.ShowDialog(Runtime.MainForm) == DialogResult.OK)
                    ptcl.ExportSet(setIndex, ems, sfd.FileName);
            }
            //Import a .eftset bundle as a new set under the Emitter Sets folder.
            private void ImportSetDialog()
            {
                var ptcl = FindPtcl(); if (ptcl == null) return;
                var ofd = new OpenFileDialog();
                ofd.Filter = "Toolbox Emitter Set (*.eftset)|*.eftset|All files (*.*)|*.*";
                if (ofd.ShowDialog(Runtime.MainForm) == DialogResult.OK)
                    ptcl.ImportSet(ofd.FileName);
            }

            //Import an image as a new texture in the shared TEXA table. Lives on the TEXA node so it is reachable
            //even when the file has no textures yet (and therefore no "Textures" folder to right-click).
            private void CreateTexture()
            {
                var ptcl = FindPtcl();
                if (ptcl == null) return;
                OpenFileDialog ofd = new OpenFileDialog();
                ofd.Filter = "Microsoft DDS|*.dds|Supported Images|*.png;*.bmp;*.tga;*.tiff|All files (*.*)|*.*";
                if (ofd.ShowDialog() == DialogResult.OK)
                    ptcl.AddTextureFromImage(ofd.FileName);
            }

            //Import an .obj as a new primitive in the shared PRMA table. Lives on the PRMA node so it is reachable
            //even when the file has no primitives yet.
            private void AddPrimitive()
            {
                var ptcl = FindPtcl();
                if (ptcl == null) return;
                OpenFileDialog ofd = new OpenFileDialog();
                ofd.Filter = "Wavefront OBJ (*.obj)|*.obj";
                if (ofd.ShowDialog() == DialogResult.OK)
                    ptcl.AddPrimitiveFromObj(ofd.FileName);
            }

            //Modal text prompt (toolbox RenameDialog). Returns null if cancelled.
            private static string PromptName(string title, string def)
            {
                using (var dlg = new Toolbox.Library.Forms.RenameDialog())
                {
                    dlg.Text = title;
                    dlg.SetString(def);
                    if (dlg.ShowDialog() != DialogResult.OK) return null;
                    string t = (dlg.textBox1.Text ?? "").Trim();
                    return t.Length == 0 ? def : t;
                }
            }

            //Which structural edit a right-click menu item performs.
            private enum EditOp { DuplicateEmitter, DeleteEmitter, AddEmitter, RenameEmitter, RenameSet, DuplicateSet, DeleteSet, AddSet }

            private void EmitterOp(EditOp op)
            {
                var ptcl = FindPtcl();
                if (ptcl == null) return;
                try
                {
                    if (op == EditOp.AddSet) //'this' is the ESTA container: add a new emitter set
                    {
                        string name = PromptName("New emitter set name", "NewEmitterSet");
                        if (name == null) return;
                        ptcl.AddEmitterSet(name);
                        ptcl.SelectSectionPath("ESTA", int.MaxValue);                  //the new (last) set
                    }
                    else if (op == EditOp.AddEmitter || op == EditOp.RenameSet ||
                             op == EditOp.DuplicateSet || op == EditOp.DeleteSet) //'this' is an ESET
                    {
                        var esta = Parent as SectionBase;
                        if (esta == null) return;
                        int setIndex = esta.ChildSections.IndexOf(this);
                        if (op == EditOp.AddEmitter)
                        {
                            string name = PromptName("New emitter name", "NewEmitter");
                            if (name == null) return;
                            ptcl.AddEmitterTo(setIndex, name);
                            ptcl.SelectSectionPath("ESTA", setIndex, int.MaxValue);    //the new (last) emitter
                        }
                        else if (op == EditOp.RenameSet)
                        {
                            string name = PromptName("Rename emitter set", Text);
                            if (name == null) return;
                            ptcl.RenameSetAt(setIndex, name);
                            ptcl.SelectSectionPath("ESTA", setIndex);
                        }
                        else if (op == EditOp.DuplicateSet) { ptcl.DuplicateEmitterSetAt(setIndex); ptcl.SelectSectionPath("ESTA", setIndex + 1); }
                        else if (op == EditOp.DeleteSet)
                        {
                            ptcl.DeleteEmitterSetAt(setIndex);
                            if (setIndex > 0) ptcl.SelectSectionPath("ESTA", setIndex - 1);  //previous set
                            else ptcl.SelectSectionPath("ESTA");                             //none left -> the Emitter Sets folder
                        }
                    }
                    else //'this' is an EMTR
                    {
                        var eset = Parent as SectionBase;
                        var esta = eset != null ? eset.Parent as SectionBase : null;
                        if (eset == null || esta == null) return;
                        int setIndex = esta.ChildSections.IndexOf(eset);
                        int emtrIndex = eset.ChildSections.IndexOf(this);
                        if (op == EditOp.DuplicateEmitter) { ptcl.DuplicateEmitterAt(setIndex, emtrIndex); ptcl.SelectSectionPath("ESTA", setIndex, emtrIndex + 1); }
                        else if (op == EditOp.DeleteEmitter)
                        {
                            ptcl.DeleteEmitterAt(setIndex, emtrIndex);
                            if (emtrIndex > 0) ptcl.SelectSectionPath("ESTA", setIndex, emtrIndex - 1);  //previous emitter
                            else ptcl.SelectSectionPath("ESTA", setIndex);                               //none before it -> parent set
                        }
                        else if (op == EditOp.RenameEmitter)
                        {
                            string name = PromptName("Rename emitter", ReadEmitterName(ptcl.data, (int)Position));
                            if (name == null) return;
                            ptcl.RenameEmitterAt(setIndex, emtrIndex, name);
                            ptcl.SelectSectionPath("ESTA", setIndex, emtrIndex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    MessageBox.Show("Emitter edit failed: " + ex.Message);
                }
            }

            private byte[] binaryDataBytes;
            public byte[] BinaryDataBytes
            {
                get
                {
                    /*   if (BinaryData == null)
                           return binaryDataBytes;
                       else if (BinaryData is BFRES)
                           return (((BFRES)BinaryData).Save());
                       else if (BinaryData is BNTX)
                           return (((BNTX)BinaryData).Save());
                       else*/

                    return binaryDataBytes;
                }
                set
                {
                    binaryDataBytes = value;
                }
            }

            public List<SectionBase> ChildSections = new List<SectionBase>();
            public byte[] data;

            public override void OnClick(TreeView treeview)
            {
                if (BinaryData is Emitter || Signature == "EMTR")
                {
                    EmitterEditorNX editor = (EmitterEditorNX)LibraryGUI.GetActiveContent(typeof(EmitterEditorNX));
                    if (editor == null)
                    {
                        editor = new EmitterEditorNX();
                        LibraryGUI.LoadEditor(editor);
                    }
                    editor.Text = Text;
                    editor.Dock = DockStyle.Fill;
                    editor.LoadEmitter((Emitter)BinaryData);
                }
                else if (Signature == "SHDB")   //GTX Shader node -> summary popup (the bundle is too big to list)
                {
                    var p = FindPtcl();
                    if (p != null) p.ShowShaderBundleSummary();
                }
            }

            public void Read(FileReader reader, Header ptclHeader, string MagicCheck = "")
            {
                Position = (uint)reader.Position;

                if (MagicCheck != "")
                    Signature = reader.ReadSignature(4, MagicCheck);
                else
                    Signature = reader.ReadString(4, Encoding.ASCII);

                SectionSize = reader.ReadUInt32();
                SubSectionOffset = reader.ReadUInt32();
                NextSectionOffset = reader.ReadUInt32();
                Unkown = reader.ReadUInt32();
                BinaryDataOffset = reader.ReadUInt32();
                Unkown3 = reader.ReadUInt32();

                if (ptclHeader.Signature == "EFTB")
                {
                    SubSectionCount = reader.ReadUInt16();
                    ushort unk = reader.ReadUInt16();
                }
                else
                {
                    SubSectionCount = reader.ReadUInt32();
                }

                Text = Signature;

                ReadSectionData(this, ptclHeader, reader);

                if (SubSectionOffset != NullOffset)
                {
                    uint tempCount = 0;

                    //Some sections will point to sub sections but have no count? (GRSN to GRSC)
                    //This will work decently for now
                    if (SubSectionCount == 0)
                    {
                        tempCount = 1;
                    }

                    reader.Seek(Position + SubSectionOffset, SeekOrigin.Begin);
                    //A TEXR (texture) owns exactly ONE GX2B data block, but every texture's GX2B blocks form a
                    //single shared chain (each block's NextSectionOffset -> the next texture's block) and a TEXR's
                    //SubSectionCount counts the blocks remaining to the end of that chain (9,8,...,1). Walking the
                    //chain here would nest every later texture's block under this one, so cap a TEXR to its own block.
                    uint subRead = SubSectionCount + tempCount;
                    if (Signature == "TEXR") subRead = 1;
                    for (int i = 0; i < subRead; i++)
                    {
                        var ChildSection = new SectionBase();
                        Nodes.Add(ChildSection);

                        ChildSection.Read(reader, ptclHeader);
                        ChildSections.Add(ChildSection);

                        if (ChildSection.NextSectionOffset == NullOffset)
                            break;
                    }
                }

                reader.Seek(Position, SeekOrigin.Begin);

                if (ChildSections.Count != 0)
                    data = reader.ReadBytes((int)SubSectionOffset);
                else if (NextSectionOffset != NullOffset)
                    data = reader.ReadBytes((int)NextSectionOffset);
                else
                    data = reader.ReadBytes((int)SectionSize);

                if (NextSectionOffset != NullOffset)
                    reader.Seek(Position + NextSectionOffset, SeekOrigin.Begin);
            }

            private void ReadSectionData(SectionBase section, Header ptclHeader, FileReader reader)
            {
                if (section.BinaryDataOffset != NullOffset)
                {
                    using (reader.TemporarySeek(section.BinaryDataOffset + section.Position, SeekOrigin.Begin))
                    {
                        BinaryDataBytes = reader.ReadBytes((int)section.SectionSize);
                    }
                }

                switch (section.Signature)
                {
                    case "TEXR":
                        section.Text = "Texture Info";
                        BinaryData = new TEXR();

                        if (SubSectionCount > 0)
                        {
                            //Set the data block first!
                            reader.Seek(SubSectionOffset + section.Position, SeekOrigin.Begin);
                            var dataBlockSection = new SectionBase();
                            dataBlockSection.Read(reader, ptclHeader, "GX2B");

                            if (dataBlockSection.BinaryDataOffset != NullOffset)
                            {
                                reader.Seek(dataBlockSection.BinaryDataOffset + dataBlockSection.Position, SeekOrigin.Begin);
                                ((TEXR)BinaryData).data = reader.ReadBytes((int)dataBlockSection.SectionSize);
                            }

                        }

                        reader.Seek(BinaryDataOffset + section.Position, SeekOrigin.Begin);
                        ((TEXR)BinaryData).Read(reader, ptclHeader);

                        break;
                    case "SHDB":
                        reader.Seek(BinaryDataOffset + section.Position, SeekOrigin.Begin);
                        section.Text = "GTX Shader";
                        reader.ReadBytes((int)section.SectionSize);
                        break;
                    case "EMTR":
                        reader.Seek(BinaryDataOffset + 16 + section.Position, SeekOrigin.Begin);
                        Text = reader.ReadString(BinaryStringFormat.ZeroTerminated);

                        reader.Seek(BinaryDataOffset + 16 + 64 + section.Position, SeekOrigin.Begin);
                        BinaryData = new Emitter();
                        ((Emitter)BinaryData).Read(reader, ptclHeader);

                        //Capture the full emitter struct so any parameter (documented or not) can be
                        //edited and written back in place on save.
                        //The struct starts at BinaryDataOffset + 16 + 64 into the section, so its true length must
                        //subtract BinaryDataOffset too; SectionSize-(16+64) over-reads into the following section's
                        //header (harmless on a clean save, but a Probe edit in that tail would corrupt it).
                        int emitterStructSize = (int)section.SectionSize - (int)section.BinaryDataOffset - (16 + 64);
                        long emitterPos = ((Emitter)BinaryData).DataPosition;
                        if (emitterStructSize > 0 && emitterPos + emitterStructSize <= reader.BaseStream.Length)
                            using (reader.TemporarySeek(emitterPos, SeekOrigin.Begin))
                                ((Emitter)BinaryData).EmitterData = reader.ReadBytes(emitterStructSize);
                        break;
                    case "ESTA":
                        section.Text = "Emitter Sets";
                        break;
                    case "ESET":
                        byte[] Padding = reader.ReadBytes(16);
                        section.Text = reader.ReadString(BinaryStringFormat.ZeroTerminated);
                        break;
                    case "GRTF":
                        if (section.BinaryDataOffset != NullOffset)
                        {
                            section.Text = "Textures";

                            reader.Seek(section.BinaryDataOffset + section.Position, SeekOrigin.Begin);
                            BinaryData = new BNTX();
                            ((BNTX)BinaryData).LoadIcons = true;
                            ((BNTX)BinaryData).FileName = "textures.bntx";
                            ((BNTX)BinaryData).Load(new MemoryStream(reader.ReadBytes((int)section.SectionSize)));
                            ((BNTX)BinaryData).IFileInfo.InArchive = true;
                            ptclHeader.BinaryTextureFile = ((BNTX)BinaryData);
                            Nodes.Add(((BNTX)BinaryData));
                        }
                        break;
                    case "PRMA":
                        break;
                    case "PRIM":
                        section.Text = "Primitive";
                        if (BinaryDataBytes != null && BinaryDataBytes.Length >= 0x54)
                        {
                            var prim = new Primitive();
                            prim.LoadMesh(BinaryDataBytes);
                            BinaryData = prim;
                            Nodes.Add(prim);
                        }
                        break;
                    case "ESFT":
                        reader.Seek(28, SeekOrigin.Current);
                        int StringSize = reader.ReadInt32();
                        section.Text = reader.ReadString(StringSize, Encoding.ASCII);
                        break;
                    case "GRSN":
                        section.Text = "Shaders";

                        if (section.BinaryDataOffset != NullOffset)
                        {
                            reader.Seek(section.BinaryDataOffset + section.Position, SeekOrigin.Begin);
                            BinaryData = reader.ReadBytes((int)section.SectionSize);
                        }
                        break;
                    case "GRSC":
                        section.Text = "Shaders 2";
                        if (section.BinaryDataOffset != NullOffset)
                        {
                            reader.Seek(section.BinaryDataOffset + section.Position, SeekOrigin.Begin);
                            BinaryData = reader.ReadBytes((int)section.SectionSize);
                        }
                        break;
                    case "G3PR":
                        if (section.BinaryDataOffset != NullOffset)
                        {
                            section.Text = "Models";

                            reader.Seek(section.BinaryDataOffset + section.Position, SeekOrigin.Begin);
                            BinaryData = new BFRES();
                            ((BFRES)BinaryData).IsParticlePrimitive = true;
                            ((BFRES)BinaryData).FileName = "model.bfres";
                            ((BFRES)BinaryData).Load(new MemoryStream(reader.ReadBytes((int)section.SectionSize)));
                            ((BFRES)BinaryData).IFileInfo = new IFileInfo();
                            ((BFRES)BinaryData).IFileInfo.InArchive = true;
                            Nodes.Add(((BFRES)BinaryData));
                        }
                        break;
                    case "GTNT":
                        if (section.BinaryDataOffset != NullOffset)
                        {
                            foreach (var node in Parent.Nodes)
                            {
                                if (node is BNTX)
                                {
                                    BNTX bntx = (BNTX)node;

                                    reader.Seek(section.BinaryDataOffset + section.Position, SeekOrigin.Begin);
                                    for (int i = 0; i < bntx.Textures.Count; i++)
                                    {
                                        var texDescriptor = new TextureDescriptor();
                                        Nodes.Add(texDescriptor);
                                        texDescriptor.Read(reader, bntx);
                                    }
                                }
                            }
                        }
                        break;
                }
            }

            public void Write(FileWriter writer, PTCL.Header header)
            {
                switch (Signature)
                {
                    case "GRSN":
                        SaveHeader(writer, header, BinaryDataBytes, 4096);
                        break;
                    case "GRSC":
                        SaveHeader(writer, header, BinaryDataBytes, 4096);
                        break;
                    case "G3PR":
                        {
                            var mem = new System.IO.MemoryStream();
                            if (BinaryData != null && ((BFRES)BinaryData).CanSave)
                            {
                                ((BFRES)BinaryData).Save(mem);
                                SaveHeader(writer, header, mem.ToArray(), 4096);
                            }
                            else
                                SaveHeader(writer, header, BinaryDataBytes, 4096);
                        }
                        break;
                    case "GRTF":
                        {
                            var mem = new System.IO.MemoryStream();
                            if (BinaryData != null)
                            {
                                ((BNTX)BinaryData).Save(mem);
                                SaveHeader(writer, header, mem.ToArray(), 4096);
                            }
                            else
                                SaveHeader(writer, header, BinaryDataBytes, 4096);
                        }
                        break;
                    case "PRIM":
                        SaveHeader(writer, header, BinaryDataBytes);
                        break;
                    case "EMTR":
                        //Write all the data first
                        long _emitterPos = writer.Position;
                        writer.Write(data);
                        foreach (var child in ChildSections)
                        {
                            child.Write(writer, header);
                        }

                        using (writer.TemporarySeek(_emitterPos + BinaryDataOffset + 16 + 64, SeekOrigin.Begin))
                        {
                            ((Emitter)BinaryData).Write(writer, header);
                        }
                        break;
                    default:
                        writer.Write(data);
                        foreach (var child in ChildSections)
                        {
                            child.Write(writer, header);
                        }
                        break;
                }


                /*      writer.Write(Signature);
                      writer.Write(SectionSize);
                      writer.Write(SubSectionOffset);
                      writer.Write(NextSectionOffset);
                      writer.Write(Unkown);
                      writer.Write(Unkown3);
                      writer.Write(SubSectionCount);*/
            }

            public class BinarySavedEntry
            {
                public long Position;
                public long _ofsData;
                public byte[] Data;
            }

            public List<BinarySavedEntry> BinariesSaved = new List<BinarySavedEntry>();

            private void SaveHeader(FileWriter writer, Header header, byte[] BinaryFile, int BinaryAlignment = 0)
            {
                if (Signature != "PRIM")
                    writer.Align(16);

                if (BinaryFile != null && BinaryFile.Length > 0)
                    SectionSize = (uint)BinaryFile.Length;

                long BasePosition = writer.Position;

                writer.WriteSignature(Signature);
                writer.Write(SectionSize);
                long _ofsChildPos = writer.Position;
                writer.Write(NullOffset); //Childern Offset for later
                long _ofsNextPos = writer.Position;
                writer.Write(NullOffset); //Next Offet for later
                writer.Write(Unkown);
                long _ofsBinaryPos = writer.Position;
                writer.Write(NullOffset); //Binary Offset for later
                writer.Write(Unkown3);
                writer.Write(SubSectionCount);

                if (ChildSections.Count > 0)
                    writer.WriteUint32Offset(_ofsChildPos, BasePosition);

                foreach (var child in ChildSections)
                {
                    if (child.BinaryData != null)
                    {
                        //Skip binaries for childern first
                        ChildHasBinary = true;
                        BinariesSaved.Add(new BinarySavedEntry()
                        {
                            Position = writer.Position,
                            _ofsData = writer.Position + 20,
                            Data = child.BinaryDataBytes,
                        });
                        child.Write(writer, header); //Save childern
                        ChildHasBinary = false; //Now all children headers have been written
                    }
                    else
                        child.Write(writer, header); //Save childern
                }

                if (!ChildHasBinary)
                {
                    if (BinaryFile != null && BinaryFile.Length > 0)
                    {
                        if (BinaryAlignment != 0)
                            writer.Align(BinaryAlignment); //Align the file
                        Console.WriteLine($"{Signature} DATA BLOCK " + writer.Position + " " + BinaryFile.Length);

                        writer.WriteUint32Offset(_ofsBinaryPos, BasePosition); //Save binary offset
                        writer.Write(BinaryFile); //Save binary data
                    }

                    foreach (var binary in BinariesSaved)
                    {
                        writer.Align(4096); //Align the file
                        Console.WriteLine($"{Signature} DATA BLOCK " + writer.Position + " " + BinaryFile.Length);

                        writer.WriteUint32Offset(binary._ofsData, binary.Position); //Save binary offset
                        writer.Write(binary.Data); //Save binary data
                    }

                    BinariesSaved.Clear();

                }

                if (NextSectionOffset != NullOffset)
                {
                    if (Signature != "PRIM")
                        writer.Align(16);

                    writer.WriteUint32Offset(_ofsNextPos, BasePosition);
                }
            }
        }
        public class TEXR : STGenericTexture
        {
            public override TEX_FORMAT[] SupportedFormats
            {
                get
                {
                    return new TEX_FORMAT[]
                    {
                        TEX_FORMAT.BC1_UNORM,
                        TEX_FORMAT.BC1_UNORM_SRGB,
                        TEX_FORMAT.BC2_UNORM,
                        TEX_FORMAT.BC2_UNORM_SRGB,
                        TEX_FORMAT.BC3_UNORM,
                        TEX_FORMAT.BC3_UNORM_SRGB,
                        TEX_FORMAT.BC4_UNORM,
                        TEX_FORMAT.BC4_SNORM,
                        TEX_FORMAT.BC5_UNORM,
                        TEX_FORMAT.BC5_SNORM,
                        TEX_FORMAT.B5G6R5_UNORM,
                        TEX_FORMAT.B8G8R8A8_UNORM_SRGB,
                        TEX_FORMAT.B8G8R8A8_UNORM,
                        TEX_FORMAT.B5G5R5A1_UNORM,
                        TEX_FORMAT.R8G8B8A8_UNORM_SRGB,
                        TEX_FORMAT.R8G8B8A8_UNORM,
                        TEX_FORMAT.R8_UNORM,
                        TEX_FORMAT.R8G8_UNORM,
                    };
                }
            }

            public TEXR()
            {
                ImageKey = "Texture";
                SelectedImageKey = "Texture";
            }

            public override void OnClick(TreeView treeView)
            {
                UpdateEditor();
            }

            public void UpdateEditor()
            {
                ImageEditorBase editor = (ImageEditorBase)LibraryGUI.GetActiveContent(typeof(ImageEditorBase));
                if (editor == null)
                {
                    editor = new ImageEditorBase();
                    editor.Dock = DockStyle.Fill;
                    LibraryGUI.LoadEditor(editor);
                }

                editor.Text = Text;
                editor.LoadProperties(GenericProperties);
                editor.LoadImage(this);
            }

            public override bool CanEdit { get; set; } = false;

            public enum SurfaceFormat : byte
            {
                INVALID = 0x0,
                TCS_R8_G8_B8_A8 = 2,
                T_BC1_UNORM = 3,
                T_BC1_SRGB = 4,
                T_BC2_UNORM = 5,
                T_BC2_SRGB = 6,
                T_BC3_UNORM = 7,
                T_BC3_SRGB = 8,
                T_BC4_UNORM = 9,
                T_BC4_SNORM = 10,
                T_BC5_UNORM = 11,
                T_BC5_SNORM = 12,
                TC_R8_UNORM = 13,
                TC_R8_G8_UNORM = 14,
                TCS_R8_G8_B8_A8_UNORM = 15,
                TCS_R5_G6_B5_UNORM = 25,
            };

            public uint TileMode;
            public uint Swizzle = 0;
            public byte WrapMode = 11;
            public byte Depth = 1;
            public uint MipCount;
            public uint CompSel;
            public uint ImageSize;
            public SurfaceFormat SurfFormat;
            public byte[] data;
            public uint TextureID;

            public bool IsReplaced = false;

            public override ToolStripItem[] GetContextMenuItems()
            {
                var items = new List<ToolStripItem>();
                items.Add(new ToolStripMenuItem("Replace", null, ReplaceAction));
                items.Add(new ToolStripMenuItem("Delete Texture", null, DeleteAction));
                items.AddRange(base.GetContextMenuItems());
                return items.ToArray();
            }

            //Remove this texture from the shared table. The texture node hangs under the "Textures" folder, which
            //hangs off the PTCL node; delete is keyed by TextureID (the value emitter samplers reference).
            private void DeleteAction(object sender, EventArgs e)
            {
                for (System.Windows.Forms.TreeNode n = Parent; n != null; n = n.Parent)
                    if (n is PTCL) { ((PTCL)n).DeleteTextureById(TextureID); return; }
            }

            private void ReplaceAction(object sender, EventArgs e)
            {
                OpenFileDialog ofd = new OpenFileDialog();
                ofd.Filter = "Microsoft DDS|*.dds|Supported Images|*.png;*.bmp;*.tga;*.tiff|All files (*.*)|*.*";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    Replace(ofd.FileName);
                    UpdateEditor();
                }
            }

            public void Replace(string FileName)
            {
                var importer = new GTXImporterSettings();
                string ext = System.IO.Path.GetExtension(FileName).ToLower();
                if (ext == ".dds")
                    importer.LoadDDS(FileName);
                else
                    importer.LoadBitMap(FileName);

                //Preserve this texture's original tiling so the game still reads it the same way.
                importer.tileMode = TileMode;

                //DDS is already BCn; bitmaps must be compressed first. (Use a DDS in the original
                //format (BC4_UNORM for the BotW flame) so the header's format stays correct.)
                if (importer.DataBlockOutput.Count == 0)
                {
                    if (importer.GenerateMipmaps)
                        importer.DataBlockOutput.Add(importer.GenerateMips());
                    else
                        importer.Compress();
                }

                //GTXSwizzle.CreateGx2Texture is the static swizzler FTEX/BFRES use (the importer class
                //PCTL resolves to has no instance CreateGx2Texture).
                var surf = GTXSwizzle.CreateGx2Texture(importer.DataBlockOutput[0], importer);

                //surf.data is swizzled mip 0; surf.mipData is the rest of the chain. The descriptor records the
                //full MipCount, so the GX2B block must hold every mip or the game reads mips 1..N as garbage.
                data = (surf.mipData != null && surf.mipData.Length > 0)
                    ? Utils.CombineByteArray(surf.data, surf.mipData)
                    : surf.data;
                Width = surf.width;
                Height = surf.height;
                ImageSize = surf.imageSize;
                MipCount = surf.numMips;
                TileMode = surf.tileMode;
                Swizzle = surf.swizzle;

                //Keep the header's format in sync with the imported data, otherwise it is read back as the
                //wrong format (e.g. BC5 data interpreted as BC4 -> garbled). NOTE: for the GAME to render it,
                //import in the ORIGINAL format (BC4_UNORM for the BotW flame) so this stays the slot's format.
                SurfaceFormat sf;
                if (Enum.TryParse<SurfaceFormat>(importer.Format.ToString(), out sf))
                    SurfFormat = sf;
                else
                {
                    //The GX2 format name has no TEXR.SurfaceFormat twin (e.g. an SRGB RGBA variant). Map by the
                    //numeric GX2 code instead of silently leaving the slot's old (now wrong) format on disk.
                    uint code = (uint)importer.Format;
                    bool mapped = false;
                    foreach (SurfaceFormat cand in Enum.GetValues(typeof(SurfaceFormat)))
                        if (Gx2FormatCode(cand) == code) { SurfFormat = cand; mapped = true; break; }
                    if (!mapped)
                        MessageBox.Show("Imported format '" + importer.Format + "' has no EFTB equivalent; the " +
                            "texture may not display correctly. Re-import in the slot's original format " +
                            "(e.g. BC4_UNORM for the BotW flame).");
                }

                IsReplaced = true;
            }
            public static GTXImporterSettings SetImporterSettings(string name)
            {
                var importer = new GTXImporterSettings();
                string ext = System.IO.Path.GetExtension(name);
                ext = ext.ToLower();

                switch (ext)
                {
                    case ".dds":
                        importer.LoadDDS(name);
                        break;
                    default:
                        importer.LoadBitMap(name);
                        break;
                }

                return importer;
            }

            public void Read(FileReader reader, Header header)
            {
                Width = reader.ReadUInt16();
                Height = reader.ReadUInt16();
                uint unk = reader.ReadUInt32();
                CompSel = reader.ReadUInt32();
                MipCount = reader.ReadUInt32();
                uint unk2 = reader.ReadUInt32();
                TileMode = reader.ReadUInt32();
                uint unk3 = reader.ReadUInt32();
                ImageSize = reader.ReadUInt32();
                uint unk4 = reader.ReadUInt32();
                TextureID = reader.ReadUInt32();
                SurfFormat = reader.ReadEnum<SurfaceFormat>(false);
                byte unk5 = reader.ReadByte();
                short unk6 = reader.ReadInt16();
                uint unk7 = reader.ReadUInt32();

            }

            public override void SetImageData(Bitmap bitmap, int ArrayLevel)
            {
                throw new NotImplementedException("Cannot set image data! Operation not implemented!");
            }

            public override byte[] GetImageData(int ArrayLevel = 0, int MipLevel = 0, int DepthLevel = 0)
            {
                uint GX2Format = (uint)GX2.GX2SurfaceFormat.T_BC5_UNORM;

                switch (SurfFormat)
                {
                    case SurfaceFormat.T_BC1_UNORM:
                        GX2Format = (uint)GX2.GX2SurfaceFormat.T_BC1_UNORM;
                        Format = TEX_FORMAT.BC1_UNORM;
                        break;
                    case SurfaceFormat.T_BC1_SRGB:
                        GX2Format = (uint)GX2.GX2SurfaceFormat.T_BC1_SRGB;
                        Format = TEX_FORMAT.BC1_UNORM_SRGB;
                        break;
                    case SurfaceFormat.T_BC2_UNORM:
                        GX2Format = (uint)GX2.GX2SurfaceFormat.T_BC2_UNORM;
                        Format = TEX_FORMAT.BC2_UNORM;
                        break;
                    case SurfaceFormat.T_BC2_SRGB:
                        GX2Format = (uint)GX2.GX2SurfaceFormat.T_BC2_SRGB;
                        Format = TEX_FORMAT.BC2_UNORM_SRGB;
                        break;
                    case SurfaceFormat.T_BC3_UNORM:
                        GX2Format = (uint)GX2.GX2SurfaceFormat.T_BC3_UNORM;
                        Format = TEX_FORMAT.BC3_UNORM;
                        break;
                    case SurfaceFormat.T_BC3_SRGB:
                        GX2Format = (uint)GX2.GX2SurfaceFormat.T_BC3_SRGB;
                        Format = TEX_FORMAT.BC3_UNORM_SRGB;
                        break;
                    case SurfaceFormat.T_BC4_UNORM:
                        GX2Format = (uint)GX2.GX2SurfaceFormat.T_BC4_UNORM;
                        Format = TEX_FORMAT.BC4_UNORM;
                        break;
                    case SurfaceFormat.T_BC4_SNORM:
                        GX2Format = (uint)GX2.GX2SurfaceFormat.T_BC4_SNORM;
                        Format = TEX_FORMAT.BC4_SNORM;
                        break;
                    case SurfaceFormat.T_BC5_UNORM:
                        GX2Format = (uint)GX2.GX2SurfaceFormat.T_BC5_UNORM;
                        Format = TEX_FORMAT.BC5_UNORM;
                        break;
                    case SurfaceFormat.T_BC5_SNORM:
                        GX2Format = (uint)GX2.GX2SurfaceFormat.T_BC5_SNORM;
                        Format = TEX_FORMAT.BC5_SNORM;
                        break;
                    case SurfaceFormat.TC_R8_G8_UNORM:
                        GX2Format = (uint)GX2.GX2SurfaceFormat.TC_R8_G8_UNORM;
                        Format = TEX_FORMAT.R8G8_UNORM;
                        break;
                    case SurfaceFormat.TCS_R8_G8_B8_A8_UNORM:
                        GX2Format = (uint)GX2.GX2SurfaceFormat.TCS_R8_G8_B8_A8_UNORM;
                        Format = TEX_FORMAT.R8G8B8A8_UNORM;
                        break;
                    case SurfaceFormat.TCS_R8_G8_B8_A8:
                        GX2Format = (uint)GX2.GX2SurfaceFormat.TCS_R8_G8_B8_A8_UNORM;
                        Format = TEX_FORMAT.R8G8B8A8_UNORM;
                        break;
                    case SurfaceFormat.TC_R8_UNORM:
                        GX2Format = (uint)GX2.GX2SurfaceFormat.TC_R8_UNORM;
                        Format = TEX_FORMAT.R8_UNORM;
                        break;
                    case SurfaceFormat.TCS_R5_G6_B5_UNORM:
                        GX2Format = (uint)GX2.GX2SurfaceFormat.TCS_R5_G6_B5_UNORM;
                        Format = TEX_FORMAT.B5G6R5_UNORM;
                        break;
                    default:
                        throw new Exception("Format unsupported! " + SurfFormat);
                }


                int swizzle = (int)Swizzle;
                int pitch = (int)0;
                uint bpp = GX2.surfaceGetBitsPerPixel(GX2Format) >> 3;

                GX2.GX2Surface surf = new GX2.GX2Surface();
                surf.bpp = bpp;
                surf.height = Height;
                surf.width = Width;
                surf.aa = (uint)0;
                surf.alignment = 0;
                surf.depth = Depth;
                surf.dim = 0x1;
                surf.format = GX2Format;
                surf.use = 0x1;
                surf.pitch = 0;
                surf.data = data;
                surf.numMips = 1;
                surf.mipOffset = new uint[0];
                surf.mipData = null;
                surf.tileMode = TileMode;
                surf.swizzle = Swizzle;
                surf.imageSize = ImageSize;

                return GX2.Decode(surf, ArrayLevel, MipLevel);
            }

            public void Write(FileWriter writer)
            {

            }
        }
        public class TextureDescriptor : TreeNodeCustom
        {
            public ulong TextureID;
            public string TexName;

            public void Read(FileReader reader, BNTX bntx)
            {
                uint Position = (uint)reader.Position; //Offsets are relative to this

                TextureID = reader.ReadUInt64();
                uint NextDesriptorOffset = reader.ReadUInt32();
                uint StringLength = reader.ReadUInt32();
                TexName = reader.ReadString(BinaryStringFormat.ZeroTerminated);

                Text = TexName + " " + TextureID.ToString("x");

                if (NextDesriptorOffset != 0)
                    reader.Seek(NextDesriptorOffset + Position, SeekOrigin.Begin);
            }
        }

        //A PRMA -> PRIM block: an indexed triangle mesh used as a particle primitive.
        //Layout (big endian, offsets relative to the block start):
        //  0x08 = vertex count, 0x3C = position offset, 0x40 = normal offset,
        //  0x4C = UV offset, 0x50 = index offset.
        //Each attribute is vertexCount * (x,y,z,w) floats (stride 16); indices are a u32 triangle list.
        public class Primitive : STGenericModel
        {
            public byte[] BlockData;   //original block, kept for export/replace
            public int VertexCount;
            public uint Hash;          //PRIM block +0x04 id; emitters reference a primitive by this hash (EmData +0x87C)
            public int Index;          //position in the file's primitive table (0 = first PRIM, 1 = second, ...)

            public bool IsReplaced;    //set when the user imports a new mesh
            public byte[] NewBlock;    //rebuilt PRIM block, spliced into the file on save

            //3D viewer plumbing (mirrors the BMD.cs viewport pattern).
            public GenericModelRenderer Renderer;
            public DrawableContainer DrawableContainer = new DrawableContainer();
            private bool DrawablesLoaded = false;

            private Viewport viewport
            {
                get { return LibraryGUI.GetObjectEditor().GetViewport(); }
                set { LibraryGUI.GetObjectEditor().LoadViewport(value); }
            }

            public Primitive() { Text = "Primitive Mesh"; }

            private static uint RU32(byte[] d, int o) { return ReadU32BE(d, o); }   //forward to the single shared BE impl
            private static float RF32(byte[] d, int o) { return ReadF32BE(d, o); }

            public void LoadMesh(byte[] block)
            {
                BlockData = block;
                Hash = RU32(block, 0x04);
                var obj = BuildRenderObject(block);
                Objects = new List<STGenericObject>() { obj };

                //Hand the mesh to a generic renderer + drawable container for the viewport.
                Renderer = new GenericModelRenderer();
                Renderer.Meshes.Add(obj);
                DrawableContainer.Name = Text;
                DrawableContainer.Drawables.Clear();
                DrawableContainer.Drawables.Add(Renderer);
            }

            //Decode a PRIM block into the toolbox's renderable mesh type.
            private GenericRenderedObject BuildRenderObject(byte[] block)
            {
                int vc = (int)RU32(block, 0x08);
                int posOff = (int)RU32(block, 0x3C);
                int nrmOff = (int)RU32(block, 0x40);
                int uvOff = (int)RU32(block, 0x4C);
                int idxOff = (int)RU32(block, 0x50);
                //Clamp the vertex count to what the block can actually hold so a malformed/truncated PRIM yields a
                //partial mesh instead of throwing and aborting the whole file open.
                int vtxBase = Math.Max(posOff, Math.Max(nrmOff, uvOff));
                if (vc < 0 || posOff < 0 || nrmOff < 0 || uvOff < 0 || vtxBase > block.Length) vc = 0;
                else vc = Math.Min(vc, (block.Length - vtxBase) / 16);
                VertexCount = vc;

                var obj = new GenericRenderedObject();
                obj.Text = "PrimitiveMesh";
                obj.ObjectName = "PrimitiveMesh";
                obj.HasPos = obj.HasNrm = obj.HasUv0 = true;

                for (int i = 0; i < vc; i++)
                {
                    var v = new Vertex();
                    v.pos = new Vector3(RF32(block, posOff + i * 16), RF32(block, posOff + i * 16 + 4), RF32(block, posOff + i * 16 + 8));
                    v.nrm = new Vector3(RF32(block, nrmOff + i * 16), RF32(block, nrmOff + i * 16 + 4), RF32(block, nrmOff + i * 16 + 8));
                    v.uv0 = new Vector2(RF32(block, uvOff + i * 16), RF32(block, uvOff + i * 16 + 4));
                    obj.vertices.Add(v);
                }

                var lod = new STGenericObject.LOD_Mesh();
                lod.IndexFormat = STIndexFormat.UInt32;
                lod.PrimativeType = STPrimitiveType.Triangles;
                //Use the explicit index count from the header (+0x38), clamped to what the block can hold; deriving
                //it from block length alone over-counts when the index buffer is followed by padding.
                int idxCount = (int)RU32(block, 0x38);
                int maxIdx = (block.Length - idxOff) / 4;
                if (idxCount < 0 || idxCount > maxIdx) idxCount = maxIdx;
                for (int i = 0; i + 3 <= idxCount; i += 3)
                {
                    int a = (int)RU32(block, idxOff + i * 4);
                    int b = (int)RU32(block, idxOff + (i + 1) * 4);
                    int c = (int)RU32(block, idxOff + (i + 2) * 4);
                    lod.faces.Add(a); lod.faces.Add(b); lod.faces.Add(c);
                    obj.faces.Add(a); obj.faces.Add(b); obj.faces.Add(c);
                }
                lod.GenerateSubMesh();
                obj.lodMeshes.Add(lod);
                return obj;
            }

            //Show the mesh in the shared 3D viewport when this node is clicked.
            public override void OnClick(TreeView treeView)
            {
                if (Runtime.UseOpenGL && !Runtime.UseLegacyGL)
                {
                    if (viewport == null)
                    {
                        viewport = new Viewport(ObjectEditor.GetDrawableContainers());
                        viewport.Dock = DockStyle.Fill;
                    }

                    if (!DrawablesLoaded)
                    {
                        ObjectEditor.AddContainer(DrawableContainer);
                        DrawablesLoaded = true;
                    }

                    viewport.ReloadDrawables(DrawableContainer);
                    LibraryGUI.LoadEditor(viewport);

                    viewport.Text = Text;
                }
            }

            public override ToolStripItem[] GetContextMenuItems()
            {
                //Only the purpose-built mesh actions. The generic STGenericWrapper items (Import / Export All /
                //Replace All / Clear) do not work on a raw PRIM block; "Export All" is the broken "Export" that
                //wrote no file.
                var items = new List<ToolStripItem>();
                items.Add(new ToolStripMenuItem("Export mesh (.obj)", null, ExportAction));
                items.Add(new ToolStripMenuItem("Replace mesh (.obj)", null, ReplaceAction));
                items.Add(new ToolStripMenuItem("Delete Primitive", null, DeletePrimAction));
                return items.ToArray();
            }

            //Remove this primitive from the shared PRMA table (keyed by its mesh hash). The Primitive node hangs
            //under its PRIM section, under PRMA, under the PTCL.
            private void DeletePrimAction(object sender, EventArgs e)
            {
                for (System.Windows.Forms.TreeNode n = Parent; n != null; n = n.Parent)
                    if (n is PTCL) { ((PTCL)n).DeletePrimitiveByHash(Hash); return; }
            }

            //Parse an .obj and rebuild a PRIM mesh block using this primitive's header as the donor template.
            //Returns null if the file has no usable geometry. Used by PTCL.AddPrimitiveFromObj.
            public byte[] ImportObjAsBlock(string path)
            {
                List<float[]> verts; List<int> indices;
                ParseObj(path, out verts, out indices);
                if (verts.Count == 0 || indices.Count < 3) return null;
                return BuildBlock(verts, indices);
            }

            private void ExportAction(object sender, EventArgs e)
            {
                var sfd = new SaveFileDialog();
                sfd.Filter = "Wavefront OBJ (*.obj)|*.obj";
                sfd.FileName = "primitive.obj";
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    STGenericObject mesh = Objects.FirstOrDefault();
                    if (mesh != null)
                        WriteObj(sfd.FileName, mesh);
                }
            }

            //Write a Wavefront OBJ directly. The toolbox's OBJ.ExportMesh() emits
            //0-based face indices (it passes a vertex base of 0), but OBJ indices are
            //1-based; that shifts every face by one vertex and tears apart ribbon-style
            //meshes whose vertices alternate between two edges. We write our own 1-based
            //file (invariant culture so the decimal point is always '.').
            private static void WriteObj(string path, STGenericObject mesh)
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("o " + mesh.Text);
                foreach (var v in mesh.vertices)
                    sb.AppendLine("v " + v.pos.X.ToString("0.######", ci) + " " + v.pos.Y.ToString("0.######", ci) + " " + v.pos.Z.ToString("0.######", ci));
                foreach (var v in mesh.vertices)
                    sb.AppendLine("vt " + v.uv0.X.ToString("0.######", ci) + " " + v.uv0.Y.ToString("0.######", ci));
                foreach (var v in mesh.vertices)
                    sb.AppendLine("vn " + v.nrm.X.ToString("0.######", ci) + " " + v.nrm.Y.ToString("0.######", ci) + " " + v.nrm.Z.ToString("0.######", ci));
                for (int i = 0; i + 3 <= mesh.faces.Count; i += 3)
                {
                    int a = mesh.faces[i] + 1, b = mesh.faces[i + 1] + 1, c = mesh.faces[i + 2] + 1;
                    sb.AppendLine("f " + a + "/" + a + "/" + a + " " + b + "/" + b + "/" + b + " " + c + "/" + c + "/" + c);
                }
                System.IO.File.WriteAllText(path, sb.ToString());
            }

            //--- Mesh replacement ---------------------------------------------------------------------
            private void ReplaceAction(object sender, EventArgs e)
            {
                var ofd = new OpenFileDialog();
                ofd.Filter = "Wavefront OBJ (*.obj)|*.obj";
                if (ofd.ShowDialog() != DialogResult.OK)
                    return;

                List<float[]> verts; List<int> indices;
                ParseObj(ofd.FileName, out verts, out indices);
                if (verts.Count == 0 || indices.Count < 3)
                {
                    MessageBox.Show("Could not read any usable triangles from that .obj.");
                    return;
                }

                NewBlock = BuildBlock(verts, indices);
                IsReplaced = true;

                //Swap the new mesh into the existing renderer so the viewport updates in place.
                var obj = BuildRenderObject(NewBlock);
                Objects = new List<STGenericObject>() { obj };
                if (Renderer != null)
                {
                    Renderer.Meshes.Clear();
                    Renderer.Meshes.Add(obj);
                    Renderer.UpdateVertexData();
                }

                MessageBox.Show("Imported " + verts.Count + " vertices / " + (indices.Count / 3) +
                                " triangles.\nSave the file to write the new mesh into the .sesetlist.");
            }

            //Rebuild a PRIM block from imported geometry. Real BotW PRIM blocks carry 3 OR 4 vertex streams
            //(position, normal, an optional extra/colour stream, then UV); each is a stride-16 buffer padded up to
            //a 0x40 boundary, followed by a u32 index list. We keep the donor header verbatim up to the first
            //stream and reproduce EXACTLY the donor's stream set (so its per-stream descriptors stay valid):
            //pos/normal/UV come from the OBJ, any extra stream is filled with a neutral white (1,1,1,1), and every
            //per-stream vertex count + offset and the index count/offset are patched. This reproduces the
            //originals' offsets exactly for both the 3- and 4-stream layouts (a 4-stream donor's extra stream must
            //keep its own offset/count or the mesh corrupts in-game).
            private byte[] BuildBlock(List<float[]> verts, List<int> indices)
            {
                int vc = verts.Count;
                const int stride = 16;
                int streamLen = AlignUp(vc * stride, 0x40);     //each vertex buffer is 0x40-aligned, as in the originals

                //The donor describes up to four streams; a slot is present when its offset field is non-zero
                //(position at 0x3C is always present). Parallel count/offset field slots, in file order.
                int[] offSlots = { 0x3C, 0x40, 0x48, 0x4C };
                int[] cntSlots = { 0x08, 0x10, 0x20, 0x28 };
                var present = new List<int>();
                for (int s = 0; s < offSlots.Length; s++)
                    if (s == 0 || RU32(BlockData, offSlots[s]) != 0) present.Add(s);
                int nStreams = present.Count;

                int posOff = (int)RU32(BlockData, 0x3C);         //donor header length (kept verbatim)
                int total = posOff + nStreams * streamLen + indices.Count * 4;

                byte[] nbk = new byte[total];
                Array.Copy(BlockData, 0, nbk, 0, posOff);        //keep the header verbatim

                //per-stream vertex counts + index count
                foreach (int s in present) WU32(nbk, cntSlots[s], (uint)vc);
                WU32(nbk, 0x38, (uint)indices.Count);

                //lay streams out in file order; write each one's new offset back into its slot
                var streamOff = new int[nStreams];
                int cur = posOff;
                for (int i = 0; i < nStreams; i++)
                {
                    streamOff[i] = cur;
                    WU32(nbk, offSlots[present[i]], (uint)cur);
                    cur += streamLen;
                }
                int idxOff = cur;
                WU32(nbk, 0x50, (uint)idxOff);

                //first stream = position, second = normal, last = UV; any stream in between (e.g. a vertex-colour
                //stream) gets a neutral white so the game reads valid data instead of a stale/garbage pointer.
                for (int i = 0; i < vc; i++)
                {
                    float[] v = verts[i]; //px,py,pz, nx,ny,nz, u,v
                    for (int si = 0; si < nStreams; si++)
                    {
                        int o = streamOff[si] + i * stride;
                        if (si == 0)                 { WF32(nbk, o, v[0]); WF32(nbk, o + 4, v[1]); WF32(nbk, o + 8, v[2]); WF32(nbk, o + 12, 1f); }
                        else if (si == 1)            { WF32(nbk, o, v[3]); WF32(nbk, o + 4, v[4]); WF32(nbk, o + 8, v[5]); WF32(nbk, o + 12, 0f); }
                        else if (si == nStreams - 1) { WF32(nbk, o, v[6]); WF32(nbk, o + 4, v[7]); WF32(nbk, o + 8, 0f); WF32(nbk, o + 12, 0f); }
                        else                         { WF32(nbk, o, 1f);   WF32(nbk, o + 4, 1f);   WF32(nbk, o + 8, 1f);   WF32(nbk, o + 12, 1f); }
                    }
                }
                for (int i = 0; i < indices.Count; i++) WU32(nbk, idxOff + i * 4, (uint)indices[i]);
                return nbk;
            }

            private static void WU32(byte[] d, int o, uint v) { WriteU32BE(d, o, v); }   //forward to the single shared BE impl
            private static void WF32(byte[] d, int o, float f) { WriteF32BE(d, o, f); }

            //Minimal Wavefront OBJ reader. Produces unified (pos,nrm,uv) vertices keyed by the OBJ
            //"v/vt/vn" corner, with faces triangulated as a fan. Invariant culture for the decimal point.
            private static void ParseObj(string path, out List<float[]> verts, out List<int> indices)
            {
                verts = new List<float[]>();
                indices = new List<int>();
                var pos = new List<float[]>();
                var nrm = new List<float[]>();
                var uv = new List<float[]>();
                var map = new Dictionary<string, int>();
                var ci = System.Globalization.CultureInfo.InvariantCulture;

                foreach (var rawLine in System.IO.File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] tok = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tok.Length == 0) continue;

                    if (tok[0] == "v" && tok.Length >= 4)
                        pos.Add(new float[] { P(tok[1], ci), P(tok[2], ci), P(tok[3], ci) });
                    else if (tok[0] == "vn" && tok.Length >= 4)
                        nrm.Add(new float[] { P(tok[1], ci), P(tok[2], ci), P(tok[3], ci) });
                    else if (tok[0] == "vt" && tok.Length >= 3)
                        uv.Add(new float[] { P(tok[1], ci), P(tok[2], ci) });
                    else if (tok[0] == "f" && tok.Length >= 4)
                    {
                        int[] corner = new int[tok.Length - 1];
                        for (int i = 1; i < tok.Length; i++)
                            corner[i - 1] = Unify(tok[i], pos, uv, nrm, map, verts);
                        for (int i = 1; i + 1 < corner.Length; i++)
                        {
                            indices.Add(corner[0]); indices.Add(corner[i]); indices.Add(corner[i + 1]);
                        }
                    }
                }
            }

            private static float P(string s, System.Globalization.CultureInfo ci)
            {
                float f; float.TryParse(s, System.Globalization.NumberStyles.Float, ci, out f); return f;
            }

            //Resolve one "v/vt/vn" face corner to a unified vertex index (creating it on first sight). The dedup
            //map is keyed by the RESOLVED absolute (v,t,n) triple, not the raw token, so relative/negative OBJ
            //indices that spell the same token but point at different vertices don't collapse together.
            private static int Unify(string corner, List<float[]> pos, List<float[]> uv, List<float[]> nrm, Dictionary<string, int> map, List<float[]> verts)
            {
                string[] p = corner.Split('/');
                int vi = RefIndex(p.Length > 0 ? p[0] : "", pos.Count);
                int ti = p.Length > 1 ? RefIndex(p[1], uv.Count) : -1;
                int ni = p.Length > 2 ? RefIndex(p[2], nrm.Count) : -1;
                string key = vi + "/" + ti + "/" + ni;
                int existing;
                if (map.TryGetValue(key, out existing)) return existing;
                float[] vp = (vi >= 0 && vi < pos.Count) ? pos[vi] : new float[] { 0, 0, 0 };
                float[] vn = (ni >= 0 && ni < nrm.Count) ? nrm[ni] : new float[] { 0, 0, 0 };
                float[] vt = (ti >= 0 && ti < uv.Count) ? uv[ti] : new float[] { 0, 0 };
                verts.Add(new float[] { vp[0], vp[1], vp[2], vn[0], vn[1], vn[2], vt[0], vt[1] });
                int idx = verts.Count - 1;
                map[key] = idx;
                return idx;
            }

            //OBJ indices are 1-based; negatives count back from the current end.
            private static int RefIndex(string s, int count)
            {
                if (string.IsNullOrEmpty(s)) return -1;
                int v;
                if (!int.TryParse(s, out v)) return -1;
                if (v > 0) return v - 1;
                if (v < 0) return count + v;
                return -1;
            }
        }
    }
}
