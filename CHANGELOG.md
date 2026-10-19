# Changelog

## [0.1.2](https://github.com/Atypical-Consulting/x402-dotnet/compare/v0.1.1...v0.1.2) (2026-10-19)


### Bug Fixes

* **aspnetcore:** normalise a null settle transaction to "" before storing and re-encoding it ([#32](https://github.com/Atypical-Consulting/x402-dotnet/issues/32)) ([#43](https://github.com/Atypical-Consulting/x402-dotnet/issues/43)) ([cf948e0](https://github.com/Atypical-Consulting/x402-dotnet/commit/cf948e09a38274256a4108daaf301e9993019fda))


### Refactor

* **aspnetcore:** make RecordAsync's resource parameter non-nullable and drop its Stryker suppression ([#34](https://github.com/Atypical-Consulting/x402-dotnet/issues/34)) ([#46](https://github.com/Atypical-Consulting/x402-dotnet/issues/46)) ([c9f6ce2](https://github.com/Atypical-Consulting/x402-dotnet/commit/c9f6ce29410c3ecb56b1e8d6053a0d95c92e8002))

## [0.1.1](https://github.com/Atypical-Consulting/x402-dotnet/compare/v0.1.0...v0.1.1) (2026-10-19)


### Bug Fixes

* add FsCheck property and header-fuzz suite, keep a null settlement transaction decodable ([#26](https://github.com/Atypical-Consulting/x402-dotnet/issues/26)) ([#29](https://github.com/Atypical-Consulting/x402-dotnet/issues/29)) ([4cb7735](https://github.com/Atypical-Consulting/x402-dotnet/commit/4cb7735a1890149bfe170f93dc3d2245325c8108))
* **site:** drop the patent-style reference numerals from the handshake figure ([#22](https://github.com/Atypical-Consulting/x402-dotnet/issues/22)) ([88ca0b4](https://github.com/Atypical-Consulting/x402-dotnet/commit/88ca0b4fc151be1349aae7b8959930482c776702))


### Documentation

* add the DocFX documentation and syntax highlighting to the site ([#31](https://github.com/Atypical-Consulting/x402-dotnet/issues/31)) ([d8a920f](https://github.com/Atypical-Consulting/x402-dotnet/commit/d8a920f4e61a1d9d0410808b716a9f8a26052b6f))

## 0.1.0 (2026-10-19)


### Features

* x402 v2 payment protocol — core, server middleware and paying client ([#1](https://github.com/Atypical-Consulting/x402-dotnet/issues/1)) ([e1de4e5](https://github.com/Atypical-Consulting/x402-dotnet/commit/e1de4e55bf1882a537bce7ca35be41c12d96281a))


### Bug Fixes

* disarm the armed NuGet push and prefix the package ids with Atypical. ([#3](https://github.com/Atypical-Consulting/x402-dotnet/issues/3)) ([ee6b364](https://github.com/Atypical-Consulting/x402-dotnet/commit/ee6b3649ce528361e3c514752cd631d66f81de8b))
* harden the libraries for the first release ([#20](https://github.com/Atypical-Consulting/x402-dotnet/issues/20)) ([86a3c10](https://github.com/Atypical-Consulting/x402-dotnet/commit/86a3c10a99b1b209872912dd401b30eaec238f6d))


### Documentation

* add the GitHub Pages site ([#19](https://github.com/Atypical-Consulting/x402-dotnet/issues/19)) ([bb186d9](https://github.com/Atypical-Consulting/x402-dotnet/commit/bb186d9e85c223e62f0c063e15c1394c154ae12e))
* use the project's social preview card as the README banner ([#4](https://github.com/Atypical-Consulting/x402-dotnet/issues/4)) ([6305e9b](https://github.com/Atypical-Consulting/x402-dotnet/commit/6305e9b1f2da2a483b38f5e435ffe6ecc8ceda73))
