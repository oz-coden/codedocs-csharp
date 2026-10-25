# Architecture

CodeDocs.CSharp documents compiler facts without inferring behavior. Its outputs are complete, deterministic snapshots of the selected build configuration.

## Dependency boundaries

- **CodeDocs.Model**: Roslyn-independent semantic documents, references, stable identifiers, source declarations, diagnostics, and build options.
- **CodeDocs.Analysis**: registers MSBuild before loading its assemblies, opens projects/solutions through MSBuildWorkspace, builds compilations, and extracts source-assembly symbols. ProjectReference projects loaded into the workspace are included.
- **CodeDocs.Rendering**: consumes only CodeDocs.Model and the .NET base libraries. `HtmlRenderer`, `MarkdownRenderer`, and `JsonRenderer` can each render the same IR directly, without querying Roslyn. `SiteRenderer.RenderAsync` remains the format-routing entry point and chooses subdirectories when multiple formats are requested.
- **CodeDocs.Cli**: validates arguments/configuration, orchestrates loading and rendering, applies incomplete-output policy, and commits output transactions.
- **CodeDocs.Tests**: executable regression suite using real Roslyn compilations and MSBuild integration fixtures.

## Symbol identity and references

A symbol ID combines a project key (relative project path and workspace project name) with the XML Documentation ID. The project key distinguishes identical fully qualified names in different projects or workspace target variants. Overload parameter signatures are part of Documentation IDs. IDs are hashed with SHA-256 for deterministic filenames and explicit anchors; display names do not determine URLs.

Only symbols included in the selected documentation snapshot receive links. Display-part references preserve links inside generic and tuple signatures. Type references preserve generic arguments, array/pointer/function-pointer components, and nullable annotations. Structural relationships describe base types, interfaces, parameter/value/return types, generic constraints, and attributes. No method-body call graph is constructed.

## Renderer responsibilities

HTML owns page layout, navigation, escaping, assets, search-index serialization and HTML paths. Markdown owns headings, escaping, code fences, multi-file/single-file layout and Markdown paths. JSON owns semantic-model serialization and its options. HTML and Markdown have separate XML renderers, so format-specific markup does not require format switches in shared code.

Shared presentation code provides symbol-to-page ownership, stable type ordering, member grouping, XML section selection, plain-text summaries and safe reference resolution. Each renderer supplies its type paths to the shared symbol index. Signatures and visibility are decided during analysis, not reconstructed by presentation services. UTF-8/LF file writing is shared; destination locking, validation and replacement remain in the CLI transaction.

The existing `SiteRenderer.Categories` and `SiteRenderer.JsonOptions` entry points remain available for compatibility. No renderer interface, plugin protocol or dependency-injection framework is required to add a format: a renderer consumes the model and the router selects it.

## Information preservation

The model retains original and expanded documentation XML, ordered XML nodes and inheritdoc provenance, resolved cref references, attributes including constructor/named arguments and return targets, parameter flags, extension targets, generic constraints and attributes, accessor attributes, delegate signatures, declaration locations, declaration headers, generated-source hints, accessor information, and implicit-declaration flags. Source declaration locations on members allow clients to group a merged partial type by declaration file.

Display filtering is applied consistently to the exported snapshot and link index. It does not change the underlying compilation. Generated declarations and compiler-synthesized API members are considered alongside source-written declarations.

Attribute extraction is isolated in `AttributeReader`, including typed constants used by attribute arguments, field constants and optional defaults. The contextual `symbol` target is retained for compatibility; the owning IR node distinguishes type, property, event, field, parameter, accessor, generic parameter or assembly attributes. Explicit `return`, `module` and associated `field` targets are stored separately. Auto-property backing-field attributes come from associated Roslyn field symbols. Field-like event backing fields are not exposed through Roslyn's public member enumeration, so their field-target attributes are obtained from bound attribute operations, including constructor, named and typed argument values. Unbound attributes in partial compilations retain their source spelling. No backing field is promoted into a normal public member.

## Deterministic rendering

Project/type/member lists and diagnostics are sorted ordinally. No wall-clock timestamp, random ID, absolute checkout path, or temporary output path is intentionally included in generated documents. Temporary directory names are random for filesystem isolation but never enter rendered content. Output uses UTF-8 without a BOM and LF newlines.

The HTML search index has both JSON and script representations. The script representation avoids fetch restrictions under file://. Search inserts result text with textContent and links only to generated local paths. XML content is escaped; arbitrary XML tags and unsafe href schemes are not emitted as active HTML.

## Output lifecycle

1. Resolve configuration and validate input.
2. Load and analyze the compiler snapshot.
3. Stop for Fatal, or Partial with keep-existing/fail.
4. Acquire an exclusive output lock and validate the destination.
5. Render every selected format into a sibling staging directory.
6. Validate required entrypoints, nonempty files, and HTML link targets/anchors.
7. Rename an existing destination to a sibling backup.
8. Rename staging to destination; restore the backup if this fails.
9. Remove the backup after the new snapshot is committed.

A transaction can render only once and cannot commit before rendering and validation have succeeded. The output marker identifies generated directories. An existing unmanaged directory requires --clean. Source/repository directories and symlink paths are rejected regardless of --clean. Previous generated files are not copied forward, so removed symbols cannot leave stale pages. Different output formats share the same transaction.

The rename sequence is recoverable for ordinary I/O failures; it is not a filesystem transaction across crashes. If backup cleanup fails, the committed snapshot remains valid and the backup is retained.

## Extension points

JSON serializes the semantic model directly and currently uses schema version implied by toolVersion. A future stable JSON schema or MCP adapter should sit above this model. Extension reverse indexes, generated-source filtering, source-repository URLs, and additional renderers can be added without coupling renderers to compiler symbols.

## Structured values and XML inheritance

Type references retain tuple element names, containing types, metadata/assembly names, array rank and element type, pointers, native-integer flags, type-parameter identity, and function-pointer return/parameter reference kinds and calling conventions. `Arguments` holds ordinary named-type generic arguments. Array/pointer/function-pointer/tuple components use their dedicated properties so nested components are not duplicated exponentially in JSON. Attribute arguments, field constants and optional parameter defaults retain typed values in addition to display strings. Arrays and typeof references are explicit nodes. Accessors retain parameter metadata, including attributes on the implicit setter value. Generic constraints have both display parts and structured kind/type entries.

XML reading is isolated from symbol extraction. Inheritdoc expands documented overrides/interfaces recursively, follows undocumented intermediate overrides, applies node-selecting XPath, then remaps parameter/type-parameter names by ordinal. Existing local sections take precedence. Invalid expressions, scalar/empty selections, unresolved sources and cycles produce warnings and remain visible as unresolved inheritdoc. XML parsing prohibits DTDs and external entity resolution. Raw XML is retained independently of rendering.

## Regression verification

The executable test suite covers semantic extraction, independent renderer entry points, associated-field attribute targets, XML inheritance/preservation, escaping, link integrity and transaction failures. `--export-snapshots <directory>` exports three fixed compiler fixtures in HTML, JSON and both Markdown layouts. `scripts/compare_renderer_baseline.py <directory>` renders the same exported IR with the historical renderer and compares file sets and bytes; the baseline commit must be present in local Git history. This isolates renderer compatibility from analyzer or project-source changes. Intentional attribute and XML corrections have separate focused regression cases.

CI runs this comparison and the complete test suite on Linux and Windows. The self-documentation workflow also installs the packaged CLI, documents the solution with all/public visibility, checks links and search coverage, and compares two independent generations byte for byte within each environment.
