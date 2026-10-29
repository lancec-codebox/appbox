# Release signing

The release APK is signed with a stable key kept only in GitHub repository secrets:
`MEDIA_HELPER_KEYSTORE_BASE64`, `MEDIA_HELPER_KEYSTORE_PASSWORD`, `MEDIA_HELPER_KEY_PASSWORD`. The key alias is not a secret: it is the existing key's `media-helper`, the workflow default (override with the repository variable `MEDUSA_KEY_ALIAS`). The secret names keep their old `MEDIA_HELPER_` prefix so the existing values keep working. Releases: tag `medusa-vX.Y.Z` on <https://github.com/lancec-codebox/appbox>.

Origin: generated on 2026-10-29 in the agent sandbox with `keytool -genkeypair -storetype PKCS12 -alias medusa -keyalg RSA -keysize 4096 -validity 36500` (CN=Medusa, O=Ref12). PKCS12 uses one password for store and key, so both password secrets hold the same value. Certificate SHA-256:
`BF:DB:CB:CE:C5:49:F8:33:A3:2D:70:DD:56:C3:89:B2:00:63:4B:47:B5:3A:52:CC:50:B1:B9:BF:8A:03:91:1E`

The values were handed over through a secret card, not committed. Keep a backup of the keystore: lose the key and installed copies can no longer be updated. Updates only install over an APK signed with the same key.
