# Architecture

CodeDocs.CSharp documents compiler facts without inferring behavior. Its outputs are complete, deterministic snapshots of the selected build configuration.

## Dependency boundaries

- **CodeDocs.Model**: Roslyn-independent semantic documents, references, stable identifiers, source declarations, diagnostics, and build options.
- **CodeDocs.Analysis**: registers MSBuild before loading its assemblies, opens projects/solutions through MSBuildWorkspace, builds compilations, and extracts source-assembly symbols. ProjectReference projects loaded into the workspace are included.
- **CodeDocs.Rendering**: consumes only CodeDocs.Model and the .NET base libraries. Produces HTML, Markdown, and JSON without querying Roslyn.
- **CodeDocs.Cli**: validates arguments/configuration, orchestrates loading and rendering, applies incomplete-output policy, and commits output transactions.
- **CodeDocs.Tests**: executable regression suite using real Roslyn compilations and MSBuild integration fixtures.

## Symbol identity and references

A symbol ID combines a project key (relative project path and workspace project name) with the XML Documentation ID. The project key distinguishes identical fully qualified names in different projects or workspace target variants. Overload parameter signatures are part of Documentation IDs. IDs are hashed with SHA-256 for deterministic filenames and explicit anchors; display names do not determine URLs.

Only symbols included in the selected documentation snapshot receive links. Display-part references preserve links inside generic and tuple signatures. Type references preserve generic arguments, array/pointer/function-pointer components, and nullable annotations. Structural relationships describe base types, interfaces, parameter/value/return types, generic constraints, and attributes. No method-body call graph is constructed.

## Information preservation

The model retains raw and expanded documentation XML, resolved cref references, attributes including constructor/named arguments and return targets, parameter flags, extension targets, generic constraints and attributes, declaration locations, declaration headers, generated-source hints, accessor information, and implicit-declaration flags. Source declaration locations on members allow clients to group a merged partial type by declaration file.

Display filtering is applied consistently to the exported snapshot and link index. It does not change the underlying compilation. Generated declarations and compiler-synthesized API members are considered alongside source-written declarations.

## Deterministic rendering

Project/type/member lists and diagnostics are sorted ordinally. No wall-clock timestamp, random ID, absolute checkout path, or temporary output path is intentionally included in generated documents. Temporary directory names are random for filesystem isolation but never enter rendered content. Output uses UTF-8 without a BOM and LF newlines.

The HTML search index has both JSON and script representations. The script representation avoids fetch restrictions under file://. Search inserts result text with textContent and links only to generated local paths. XML content is escaped; arbitrary XML tags and unsafe href schemes are not emitted as active HTML.

## Output lifecycle

1. Resolve configuration and validate input.
2. Load and analyze the compiler snapshot.
3. Stop for Fatal, or Partial with keep-existing/fail.
4. Acquire an exclusive output lock and validate the destination.
5. Render every selected format into a sibling staging directory.
6. Validate required entrypoints and nonempty files.
7. Rename an existing destination to a sibling backup.
8. Rename staging to destination; restore the backup if this fails.
9. Remove the backup after the new snapshot is committed.

The output marker identifies generated directories. An existing unmanaged directory requires --clean. Source/repository directories and symlink paths are rejected regardless of --clean. Previous generated files are not copied forward, so removed symbols cannot leave stale pages. Different output formats share the same transaction.

The rename sequence is recoverable for ordinary I/O failures; it is not a filesystem transaction across crashes. If backup cleanup fails, the committed snapshot remains valid and the backup is retained.

## Extension points

JSON serializes the semantic model directly and currently uses schema version implied by toolVersion. A future stable JSON schema or MCP adapter should sit above this model. Extension reverse indexes, generated-source filtering, source-repository URLs, and additional renderers can be added without coupling renderers to compiler symbols.
