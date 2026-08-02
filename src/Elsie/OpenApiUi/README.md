# Bundled offline Scalar API reference UI

`standalone.js` is the **`@scalar/api-reference`** standalone browser bundle
(version `1.64.0`, MIT license, https://github.com/scalar/scalar), embedded into the
`Elsie` host assembly as an EmbeddedResource and served when
`ElsieOpenApiHostOptions.UseScalarCdn = false` (fully offline API reference UI).

- Update: `./tools/UpdateScalarAssets.sh [version]` (defaults to the pinned version).
- The bundle is self-contained: styles are inlined and there are no runtime CDN fetches.
- Do not hand-edit the bundle. Keep `tools/UpdateScalarAssets.sh` in sync with the pinned
  version and commit both together.
