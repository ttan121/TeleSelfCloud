using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace TeleSelfCloud.Core.Transfers;

public static class ManifestValidator
{
	public static void ValidateStructure(FileManifest manifest)
	{
		ArgumentNullException.ThrowIfNull(manifest);
		if (manifest.SchemaVersion != 1)
		{
			throw new InvalidDataException("Unsupported manifest schema version.");
		}
		bool flag = string.IsNullOrWhiteSpace(manifest.FileId) || string.IsNullOrWhiteSpace(manifest.FileName);
		if (!flag)
		{
			string fileName = manifest.FileName;
			bool flag2 = ((fileName == "." || fileName == "..") ? true : false);
			flag = flag2;
		}
		if (flag || !string.Equals(Path.GetFileName(manifest.FileName), manifest.FileName, StringComparison.Ordinal) || manifest.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
		{
			throw new InvalidDataException("Manifest identity or file name is invalid.");
		}
		if (manifest.LogicalSize < 0 || manifest.PartSizeBytes <= 0)
		{
			throw new InvalidDataException("Manifest sizes are invalid.");
		}
		if (manifest.Revision < 0 || !IsSafeFolderPath(manifest.FolderPath))
		{
			throw new InvalidDataException("Manifest revision or folder path is invalid.");
		}
		ValidateHash(manifest.TotalSha256, "total");
		if (manifest.Parts is null || manifest.Parts.Count == 0)
		{
			throw new InvalidDataException("Manifest must include a part, including for an empty file.");
		}
		long num = manifest.LogicalSize;
		var encryption = manifest.Encryption;
		if (encryption is not null)
		{
			if (encryption.Version != 1 || encryption.PayloadSize <= 0 || encryption.RecoveryKey is null)
			{
				throw new InvalidDataException("Manifest encryption descriptor is invalid or unsupported.");
			}
			ValidateHash(encryption.PayloadSha256, "encrypted payload");
			PassphraseKeyEnvelope recoveryKey = encryption.RecoveryKey;
			if (recoveryKey.Version != 1 || recoveryKey.Kdf != "PBKDF2-SHA256" || recoveryKey.Iterations != 600000 || recoveryKey.Cipher != "AES-256-GCM" || !HasBase64Length(recoveryKey.Salt, 16) || !HasBase64Length(recoveryKey.Nonce, 12) || !HasBase64Length(recoveryKey.Ciphertext, 32) || !HasBase64Length(recoveryKey.Tag, 16))
			{
				throw new InvalidDataException("Manifest recovery key envelope is invalid or unsupported.");
			}
			num = encryption.PayloadSize;
		}
		long num2 = 0L;
		for (int i = 0; i < manifest.Parts.Count; i++)
		{
			PartRecord partRecord = manifest.Parts[i];
			if (partRecord is null)
				throw new InvalidDataException("Manifest contains a null part entry.");
			if (partRecord.Index != i || partRecord.Offset != num2 || partRecord.Length < 0 || partRecord.Length > manifest.PartSizeBytes)
			{
				throw new InvalidDataException("Manifest part indices, offsets, or sizes are inconsistent.");
			}
			if (partRecord.Confirmed != !string.IsNullOrWhiteSpace(partRecord.RemoteId))
			{
				throw new InvalidDataException("Manifest part confirmation state and remote reference are inconsistent.");
			}
			if (num != 0L && partRecord.Length == 0L)
			{
				throw new InvalidDataException("A non-empty file cannot contain an empty part.");
			}
			ValidateHash(partRecord.Sha256, $"part {partRecord.Index}");
            if (partRecord.CopySource is { } copy && (manifest.Committed || partRecord.Confirmed ||
                copy.AccountId != manifest.AccountId || string.IsNullOrWhiteSpace(copy.AccountId) || string.IsNullOrWhiteSpace(copy.OwnerFileId) ||
                copy.OwnerFileId == manifest.FileId || copy.OwnerFileId.Length > 200 || copy.OwnerFileId.IndexOfAny(['|', '\r', '\n']) >= 0 || copy.Index != partRecord.Index ||
                string.IsNullOrWhiteSpace(copy.RemoteId)))
                throw new InvalidDataException("The local document-copy intent has invalid ownership or state.");
			num2 = checked(num2 + partRecord.Length);
		}
		if (num == 0L && (manifest.Parts.Count != 1 || manifest.Parts[0].Length != 0L))
		{
			throw new InvalidDataException("An empty file must have exactly one empty part.");
		}
		if (num2 != num)
		{
			throw new InvalidDataException("Manifest part sizes do not match the file payload size.");
		}
		if (manifest.Committed && manifest.Parts.Any((PartRecord p) => !p.Confirmed || string.IsNullOrWhiteSpace(p.RemoteId)))
		{
			throw new InvalidDataException("A committed remote manifest must reference every confirmed remote part.");
		}
	}

	public static bool HashMatches(string expectedHex, byte[] actual)
	{
		return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHex), actual);
	}

	private static void ValidateHash(string value, string label)
	{
		if (string.IsNullOrWhiteSpace(value))
			throw new InvalidDataException("Manifest " + label + " SHA-256 is missing.");
		try
		{
			if (Convert.FromHexString(value).Length != 32)
			{
				throw new InvalidDataException("Manifest " + label + " SHA-256 has an invalid length.");
			}
		}
		catch (FormatException innerException)
		{
			throw new InvalidDataException("Manifest " + label + " SHA-256 is malformed.", innerException);
		}
	}

	private static bool HasBase64Length(string value, int expectedLength)
	{
		if (string.IsNullOrWhiteSpace(value)) return false;
		try
		{
			return Convert.FromBase64String(value).Length == expectedLength;
		}
		catch (FormatException)
		{
			return false;
		}
	}

	private static bool IsSafeFolderPath(string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return true;
		}
		if (value.StartsWith('/') || value.StartsWith('\\') || Path.IsPathRooted(value))
		{
			return false;
		}
		return value.Replace('\\', '/').Split('/').All((string segment) =>
		{
			bool flag = !string.IsNullOrWhiteSpace(segment);
			if (flag)
			{
				bool flag2 = ((segment == "." || segment == "..") ? true : false);
				flag = !flag2;
			}
			return flag && segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
		});
	}
}

