/**
 * Prettier — the NON-C# file types only.
 *
 * C# IS NOT FORMATTED BY PRETTIER AND CANNOT BE. Prettier has no C# parser and no
 * maintained plugin provides one. C# is formatted by the Roslyn formatter (the
 * ms-dotnettools.csharp extension in the editor) and checked by `dotnet format`, a
 * blocking CI stage, both reading `.editorconfig`. Those two agree with each other by
 * construction; a third formatter would not. So: do NOT put "cs" or "*.cs" into
 * `prettier.documentSelectors` in VS Code settings — Prettier will register itself as a
 * formatter for .cs files and then fail with "No parser could be inferred", which reads
 * as a broken Prettier rather than as a file type it was never asked to handle.
 *
 * endOfLine is "auto" on purpose. `.gitattributes` pins eol=lf for *.cs, *.sh and the
 * hooks, but NOT for *.uss/*.uxml, so with core.autocrlf=true those sit CRLF in the
 * working tree. Prettier's "lf" default would rewrite every line of every file on the
 * first save — invisible in `git diff` (git normalises on commit) but a whole-file churn
 * on disk and in any open diff view. "auto" keeps whatever the file already uses.
 *
 * @type {import("prettier").Config}
 */
const config = {
    plugins: ["@prettier/plugin-xml"],
    printWidth: 100,
    tabWidth: 4,
    endOfLine: "auto",

    overrides: [
        {
            // UXML is XML. Unity does not care about whitespace between elements, and
            // "ignore" is what lets attribute-heavy elements wrap instead of running off
            // the screen. It does mean text nodes may be re-indented — UXML has almost
            // none, but check any <ui:Label> that carries literal text with meaningful
            // spacing. It ALSO strips blank lines between sibling elements, which several
            // of these files use to group views; that grouping does not survive a format.
            //
            // The two values below were measured against the 9 UXML files in the tree
            // (717 lines) rather than picked. printWidth: at 100 the output is 1136 lines
            // and 823 lines change — every compact element explodes. At 200 it is 733
            // lines and 310; at 240, 719 and 294. 200 is where the curve flattens.
            // xmlSelfClosingSpace: false because the tree writes `/>`, not ` />`, and
            // flipping it churns nearly every self-closing element for nothing.
            files: ["*.uxml"],
            options: {
                parser: "xml",
                printWidth: 200,
                xmlWhitespaceSensitivity: "ignore",
                xmlSelfClosingSpace: false,
                singleAttributePerLine: false,
            },
        },
        {
            // USS is CSS minus a few features plus Unity's `-unity-*` properties and
            // `resource()` / `url()`. postcss parses all of that; the same parser already
            // backs the `lint:uss` stylelint gate in package.json, so the two agree.
            //
            // Prettier EXPANDS one-line rules (`.x { color: red; }` -> three lines). That
            // is a real diff on the existing sheets, not a no-op — it is style only, but
            // format-on-save will produce it the first time you touch such a file.
            files: ["*.uss"],
            options: {
                parser: "css",
            },
        },
        {
            files: ["*.json", "*.jsonc", "*.asmdef", "*.asmref"],
            options: {
                parser: "json",
                tabWidth: 2,
            },
        },
    ],
};

export default config;
