// Windows protects saved API keys and tokens with DPAPI (ProtectedData). macOS and Linux have no equivalent in the runtime, so the same calls
// are answered here with AES-GCM and a random key kept in the user's own config folder (mode 600). That keeps secrets off disk in plain text,
// but it is not a keychain: anything running as the same user can read the key. A Keychain / libsecret backend is the next step.
using System.IO;

namespace System.Security.Cryptography
{
    public enum DataProtectionScope { CurrentUser = 0, LocalMachine = 1 }
    public static class ProtectedData
    {
        static byte[] key;
        static byte[] Key()
        {
            if (key != null) return key;
            string dir = Environment.GetEnvironmentVariable("LAICA_CONFIG_DIR");
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LAICA");
            Directory.CreateDirectory(dir); string file = Path.Combine(dir, "secret.key");
            if (!File.Exists(file))
            {
                var k = RandomNumberGenerator.GetBytes(32); File.WriteAllBytes(file, k);
                try { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (Exception) { }
            }
            return key = File.ReadAllBytes(file);
        }
        public static byte[] Protect(byte[] data, byte[] entropy, DataProtectionScope scope)
        {
            var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[data.Length];
            using (var g = new AesGcm(Key(), 16)) g.Encrypt(nonce, data, cipher, tag, entropy);
            var o = new byte[12 + 16 + cipher.Length]; Buffer.BlockCopy(nonce, 0, o, 0, 12); Buffer.BlockCopy(tag, 0, o, 12, 16); Buffer.BlockCopy(cipher, 0, o, 28, cipher.Length); return o;
        }
        public static byte[] Unprotect(byte[] data, byte[] entropy, DataProtectionScope scope)
        {
            if (data == null || data.Length < 28) throw new CryptographicException("The saved secret is damaged.");
            var nonce = new byte[12]; var tag = new byte[16]; var cipher = new byte[data.Length - 28]; var plain = new byte[cipher.Length];
            Buffer.BlockCopy(data, 0, nonce, 0, 12); Buffer.BlockCopy(data, 12, tag, 0, 16); Buffer.BlockCopy(data, 28, cipher, 0, cipher.Length);
            using (var g = new AesGcm(Key(), 16)) g.Decrypt(nonce, cipher, tag, plain, entropy); return plain;
        }
    }
}
