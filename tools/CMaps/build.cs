// Regenerates the predefined CJK CMaps in src/PdfPecker/Rendering/Fonts/CMaps from Adobe's CMap resources.
//
// Usage (from the repository root, .NET 10 SDK):
//   dotnet run tools/CMaps/build.cs -- <folder with Adobe's CMap files> src/PdfPecker/Rendering/Fonts/CMaps
//
// The files are Adobe's CMap resources (https://github.com/adobe-type-tools/cmap-resources, BSD-3-Clause), as the
// poppler-data 0.4.12 package (https://poppler.freedesktop.org/) ships them in its cMap folder. The folder given may be
// that cMap folder or a checkout of cmap-resources: each file is looked for in the subfolders whose names start with
// "Adobe-<ordering>". The tool checks each file against the hashes below, so a newer CMap cannot change the output
// unnoticed; when moving to newer files on purpose, update the hashes and the version named here in the same change.
//
// The list is the predefined CMaps of ISO 32000-2 table 116 for the four Adobe orderings, the CMaps they name with
// usecmap, and Adobe-<ordering>-UCS2, which maps each CID of the ordering to Unicode for text extraction. Each output
// file holds one ordering's CMaps, unmodified, Brotli-compressed together: for each file a line with its name, a line
// with its length in bytes, and then its bytes.
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

var files = new (string Ordering, string Name, string Sha256)[]
{
    ("GB1", "GB-EUC-H", "9c2b268c5fbcc9a23aa76d2f199c97fa9fbfcc0ef43f9f320bb367a6b0a47b20"),
    ("GB1", "GB-EUC-V", "5acf07ba8e65be8aad1c7a4535574e41cb326abd295e9e5c1eb3e96037260397"),
    ("GB1", "GBpc-EUC-H", "ec6132762b28650eb0c8483ea9387af39acb9d8aca432cb1e4f79a1058e1cd40"),
    ("GB1", "GBpc-EUC-V", "5fec79fd4f5f95d7638a150eec9b551268b8c29103446f0eee26866769ff384f"),
    ("GB1", "GBK-EUC-H", "6c88b308f122966de14696d819800bc89be2538fa14fa41f474e36b20cea11c6"),
    ("GB1", "GBK-EUC-V", "73381a5bc2d0aacbf6b2ec211f34e042e7c74895342fcee073b0f9be2dbc8703"),
    ("GB1", "GBKp-EUC-H", "d6ef0e6c393bd065f5b646b7dc87fe8df88f3608a9264fb5727b552418352d5a"),
    ("GB1", "GBKp-EUC-V", "43c8fa12dcef0b1b584fdd077996fff388fc0e9a9d332e08556640ceefa9fa0e"),
    ("GB1", "GBK2K-H", "c7693cc2b7b235dad5ecd428ecc0d56cf9e5245c05eb992816a6cdcb446c97b1"),
    ("GB1", "GBK2K-V", "80a5a087821c9f6829fadafc971d5bdae9bbc4b54714733fe22c64802b4301ae"),
    ("GB1", "UniGB-UCS2-H", "d2b100a68c016190347c6e0ecee3e7d6d1accd7395d7e803bf88b7ffef156725"),
    ("GB1", "UniGB-UCS2-V", "d94d2e56a9a5cb89e0a68c31eba1ff40aa604b3a504be844c67de64af64eec96"),
    ("GB1", "UniGB-UTF16-H", "4edbe1789ffb0564c14e1b245bbd01eb56bc41326f75d8b041de6847d2ee1d20"),
    ("GB1", "UniGB-UTF16-V", "7d40dc464b46ad8da9bf4d063dae76b0dc190e29805cbd2826e1599e4b432001"),
    ("GB1", "Adobe-GB1-UCS2", "368b40ec05568faf323cfbbddf23c60d566bbc88e366981eb6541b026381a3b8"),
    ("CNS1", "B5pc-H", "7a2af623d66158fd9b2eae590d3b0373b2e73125f008befda26c40a6c5d396fb"),
    ("CNS1", "B5pc-V", "f1b09f3752437a802f0992297a18361fd3bceed8e208cfbb97f2e6c035a29d2e"),
    ("CNS1", "HKscs-B5-H", "46b4d44a0d38cf188a88f42688f10a03aed8d67be80c141a1404abacc01ee191"),
    ("CNS1", "HKscs-B5-V", "1f6f7f06216987d973c18e23d1a8c79044d9f1b301e01c738867ca1fdf50e237"),
    ("CNS1", "ETen-B5-H", "b6e5e42d94f6642842f6a42445c1df29d3874c6d1a91a38a05c9d7e4b57f0ce6"),
    ("CNS1", "ETen-B5-V", "0e6f041edd60516e92c9470254c4d45522b4cf079f1809b82862d43a3f1e1663"),
    ("CNS1", "ETenms-B5-H", "051a38392481d6069608f41962ef9393e142797a8977d7f7fbd534b051a6e231"),
    ("CNS1", "ETenms-B5-V", "477e42501dd0c27301b992ab004d6044bf5406277ee7a353d3b04cc08092a9e0"),
    ("CNS1", "CNS-EUC-H", "61a5163ddf2dcbeed5efc4c8e9a612c7b3c5a0c062df75b2b3d73e3e46e7a299"),
    ("CNS1", "CNS-EUC-V", "91faeda86c64df62c57b434a5b1322e1a6236c8520db3d4ee58b6ae5fee4c01e"),
    ("CNS1", "UniCNS-UCS2-H", "fe9c7f1557d58875bc4a16edf377ee0d8c579eaee778135adefbbed1f868c017"),
    ("CNS1", "UniCNS-UCS2-V", "745fd7a922bedd2d124006a4b5e6a909aaaa9f11eebefbb0477c22a01439a3a8"),
    ("CNS1", "UniCNS-UTF16-H", "074d175c8c6827184d064f2c48cbe0ba4957bd525fd864184717518acf4db970"),
    ("CNS1", "UniCNS-UTF16-V", "d21175d1f8a083b606a9a1e17fa98d1cb901a0e833e19d47a6c08fb452c91c11"),
    ("CNS1", "Adobe-CNS1-UCS2", "8375afd535e153a7e3bcf448be93e5012b26229547a20d52520e46e3c386351f"),
    ("Japan1", "83pv-RKSJ-H", "77f7a0f11fe2ee160baa6446b47b8a66cda41aac4ab777fb92c192198a521377"),
    ("Japan1", "90ms-RKSJ-H", "e0b104bcc8dfb3c2d3e025e12c59182d7e0ee1b5df6f55bc8550d4b3fc83ecd3"),
    ("Japan1", "90ms-RKSJ-V", "a588f7d20d0c8dbf09693480e62bf3ab80b7453858e4425d4b47650d48e7757b"),
    ("Japan1", "90msp-RKSJ-H", "10b15c3b208d35f9b2764b0dd00e52de59170aa3f102387e7c388dfaa2290fc0"),
    ("Japan1", "90msp-RKSJ-V", "05b5adf56096c0f1baeb267ec63f4018c85e3c3cff01f3457ffa10672a225c09"),
    ("Japan1", "90pv-RKSJ-H", "bda3f87262c61ef5a34097737f7306fb160d769f357b72ad251d11384a3848d2"),
    ("Japan1", "Add-RKSJ-H", "630d4689f3d17000208452c855d9360dc1f769f102b7d29c17ea11973b28aaa7"),
    ("Japan1", "Add-RKSJ-V", "5664073e91e61009d72d5f007207f29bb8561c05939f3b185909ab92424bf039"),
    ("Japan1", "EUC-H", "5cceeaf017e9f813079bbc17d8e750072e50042a01e521c9f9c18efabd596cba"),
    ("Japan1", "EUC-V", "d35c5ce976385980244040037152d92d321b738bca885ad0976c10658cd0acab"),
    ("Japan1", "Ext-RKSJ-H", "00cd02206daf64e633d1c13b979fb20652a554d87fd9f2f0ca94065156ea8b71"),
    ("Japan1", "Ext-RKSJ-V", "8a84926990144b043f2e5ebda194bf3560cc5f29096cafab8a6079a9d86e2d6b"),
    ("Japan1", "H", "f90323c3fd16e107b02c888167739f210af7144beba1bda83949069535db3346"),
    ("Japan1", "V", "1ccdbf3451794ddb929a9e2e43f562b20a8d9de5ec44174317c8a149ab498aa1"),
    ("Japan1", "UniJIS-UCS2-H", "05d064e6b373edf83890e26d7d2a2339482fad265eaf0a2b92ab528af33b5a54"),
    ("Japan1", "UniJIS-UCS2-V", "b487b2602348ef9c69222fd1a82cd45eea4f756d98e974b94183390c9bd8b6c3"),
    ("Japan1", "UniJIS-UCS2-HW-H", "782f9d14dffa2a538302c8aa02b7d4650ff41d3bbe49215546c66a786be92bed"),
    ("Japan1", "UniJIS-UCS2-HW-V", "eb1fe9bd104770efb0fda13b211fee88cc5487a653e5cdf6d89bb782336e7673"),
    ("Japan1", "UniJIS-UTF16-H", "5cf77ed38c25262dba845676738a887afe45e44425c3dd2489334c4d8c510fcb"),
    ("Japan1", "UniJIS-UTF16-V", "03bfb4e91452da537f347b8676e52adfa5dd10f8b920fa2cc34d343580354056"),
    ("Japan1", "Adobe-Japan1-UCS2", "6a9693361647a37996312cc57071bb79f8c06411207be7c730a83fda1254cd82"),
    ("Korea1", "KSC-EUC-H", "90e9250027a7a84fc7a2542f91fe61e3a71299214aaa75c617e12a55dfaa0943"),
    ("Korea1", "KSC-EUC-V", "a51f69ff435ff83b3c200b2043572492be05ac08e98f4662f3938b3f6a1b9a6c"),
    ("Korea1", "KSCms-UHC-H", "f3e3719b2db01f1c27e5f8b85cfec0d6f018239fb999cd03bbe10084a9f4645a"),
    ("Korea1", "KSCms-UHC-V", "1ca4c9a90aa3079ffdc8ceb7e288f034d0afbc7142a7fae705416368fc581186"),
    ("Korea1", "KSCms-UHC-HW-H", "129c4892d7d21a5f188af469d06673994c9395bd61c33987a0652261d27a7f7b"),
    ("Korea1", "KSCms-UHC-HW-V", "b6052023250b12413fe7ac8552d64362720374df3c0d1beb2406922856a504fa"),
    ("Korea1", "KSCpc-EUC-H", "6d2ec35d3aad68c4cba6d93849d8b7b9b8c34572c50b7361ccb6bf338c36f330"),
    ("Korea1", "UniKS-UCS2-H", "bb28e3e84154f49c311128a5700cc0187ce4957b10f18dee88089002db7e5501"),
    ("Korea1", "UniKS-UCS2-V", "76c08c4aace48d92a252e686f9d748ebde009337b59361ec286c4bdc037e5933"),
    ("Korea1", "UniKS-UTF16-H", "0a1dc38d8dd4e1f55b5a3ab978a2b091109f88f4d08587bbb7864370cda8cfd5"),
    ("Korea1", "UniKS-UTF16-V", "681aecd86956265504f7875af43eae3985ab263175aa782baa884b0f514f2aa6"),
    ("Korea1", "Adobe-Korea1-UCS2", "45bcf869acdcec2507f75836919e589e7137b9f9dbaa8e4975747035024248ef"),
};

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run tools/CMaps/build.cs -- <folder with Adobe's CMap files> <output folder>");
    return 2;
}
string src = args[0], dst = args[1];
Directory.CreateDirectory(dst);
foreach (var ordering in files.Select(f => f.Ordering).Distinct())
{
    var archive = new MemoryStream();
    foreach (var (_, name, sha256) in files.Where(f => f.Ordering == ordering))
    {
        string? path = Directory.EnumerateDirectories(src, "Adobe-" + ordering + "*", SearchOption.AllDirectories)
            .Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists)
            ?? Directory.EnumerateDirectories(src, "Adobe-" + ordering + "*", SearchOption.AllDirectories)
                .Select(d => Path.Combine(d, "CMap", name)).FirstOrDefault(File.Exists);
        if (path == null)
        {
            Console.Error.WriteLine($"{name} (Adobe-{ordering}) was not found under {src}.");
            return 1;
        }
        var bytes = File.ReadAllBytes(path);
        string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (actual != sha256)
        {
            Console.Error.WriteLine($"{path} is not the pinned version (SHA-256 {actual}); see the comment at the top of build.cs.");
            return 1;
        }
        archive.Write(Encoding.ASCII.GetBytes(name + "\n" + bytes.Length + "\n"));
        archive.Write(bytes);
    }
    string output = Path.Combine(dst, ordering + ".cmaps.br");
    using (var fs = File.Create(output))
    using (var br = new BrotliStream(fs, CompressionLevel.SmallestSize))
        br.Write(archive.ToArray());
    Console.WriteLine($"{ordering}: {archive.Length} bytes -> {new FileInfo(output).Length} bytes");
}
return 0;
