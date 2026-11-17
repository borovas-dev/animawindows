# Signing certificate

`animawindows-signing.cer` is the public half of the self-signed certificate (`CN=animawindows`, valid until 2036-11-14, SHA-1 thumbprint `BF92A1C73205DA153FF43EA4DFB7095F1DAE8913`) that CI uses to sign every MSIX. The private key lives only in the repository secrets `MSIX_CERT_PFX_BASE64` / `MSIX_CERT_PASSWORD`.

Trust it once per Windows machine (elevated PowerShell), then any CI build installs by double-click:

```powershell
Import-Certificate -FilePath .\animawindows-signing.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```
