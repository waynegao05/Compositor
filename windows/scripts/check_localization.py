#!/usr/bin/env python3
"""Check the Windows display catalog without .NET or third-party modules.

This is a deliberately bounded C# scanner, not a compiler: literal T/F calls,
constant concatenations, conditional/switch branches, inline translated arrays,
and the display arguments of the wrappers below are checked. Runtime values
(enum names, dictionaries, history descriptions, variable arrays, external error
messages and user text) still require a UI review. --show-dynamic lists those
boundaries; --audit-ui lists suspicious raw UI assignments for human review.
Neither informational list is a claim of complete translation coverage.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import json
from pathlib import Path
import re
import sys


# Argument positions are zero based. Keep these in sync with display wrappers;
# notably Command's shortcut ID, TextPrompt's initial text and file paths are NOT
# translation keys. Local helpers are scoped to their defining source file.
GLOBAL_WRAPPERS = {
    "L10n.T": (0,), "L10n.F": (0,),
    "UiText.T": (0,), "UiText.F": (0,),
    "TextPrompt.Ask": (1, 2), "TextDialog.Ask": (1,),
    "ConfirmDialog.Ask": (1, 2, 3, 4),
    "ColorPickerDialog": (0,), "ColorPickerDialog.Pick": (1,), "ColorSwatch": (0,),
}
LOCAL_WRAPPERS = {
    "MainWindow.cs": {
        "Command": (0,), "LayerCommand": (0,), "ToolItem": (0,),
        "ViewButton": (0, 1), "Toggle": (0,), "ToggleSnapTo": (1,), "Say": (0,),
    },
    "AdjustmentDialog.cs": {"Add": (1,), "Swatch": (1, 4, 5), "Check": (1,), "Row": (0,)},
    "FilterDialog.cs": {"Add": (1,), "Swatch": (1, 5, 6), "Check": (1,)},
    "EffectDialog.cs": {"Add": (1,), "Colour": (1,)},
    "CameraRawPanel.cs": {"Add": (1,), "Heading": (0,), "Choice": (0, 2)},
    "DitherDialog.cs": {"Add": (1,), "Heading": (1,), "Choice": (1, 3), "Text": (1,), "Swatch": (0, 3, 4)},
    **{name + ".cs": {"Row": (0,)} for name in (
        "TextDialog", "ImageSizeDialog", "CanvasSizeDialog", "NewDocumentDialog",
        "GridSettingsDialog", "GuideDialog", "TrimDialog")},
}

# These enum identifiers are explicitly displayed through L10n.T(value.ToString()).
# Do not add arbitrary model enums: persisted names and command IDs are not UI.
DISPLAY_ENUMS = {
    "Document/BrushEdits.cs": {"HealingMode"},
    "Document/FilterEdits.cs": {"FilterKind"},
    "Document/GradientEdits.cs": {"GradientShape"},
    "Document/DitherEdits.cs": {"DitherStyle"},
    "Document/LevelsEdits.cs": {"LevelsAuto"},
    "Document/ColorRangeSession.cs": {"Picking"},
    "Format/Enums.cs": {"ColorRange", "ShapeKind", "GuideAxis"},
}

TOKEN = re.compile(
    r"(?P<comment>//[^\n]*|/\*[\s\S]*?\*/)"
    r'|(?P<raw>(?:\$+)?(?P<delimiter>"{3,})[\s\S]*?(?P=delimiter))'
    r'|(?P<string>(?:\$@|@\$|@|\$)?"(?:""|\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\')'
    r"|(?P<word>[A-Za-z_][A-Za-z_0-9]*)"
    r"|(?P<symbol>=>|\?\?|\?\.|==|!=|&&|\|\||\S)", re.MULTILINE,
)
ESCAPE = re.compile(r"\\(?:[abfnrtv0\\\"']|u[0-9a-fA-F]{4}|U[0-9a-fA-F]{8}|x[0-9a-fA-F]{1,4})")
ESCAPES = dict(zip("abfnrtv0\\\"'", "\a\b\f\n\r\t\v\0\\\"'"))


@dataclass(frozen=True)
class Token:
    text: str
    kind: str
    line: int

    @property
    def literal(self) -> str | None:
        if self.kind != "string" or self.text.startswith(("$", "@$", "'")):
            return None
        if self.text.startswith('@"'):
            return self.text[2:-1].replace('""', '"')
        return ESCAPE.sub(lambda m: ESCAPES[m[0][1:]] if m[0][1:] in ESCAPES
                          else chr(int(m[0][2:], 16)), self.text[1:-1])


@dataclass(frozen=True)
class Finding:
    path: Path
    line: int
    message: str

    def display(self, root: Path) -> str:
        try:
            name = self.path.relative_to(root)
        except ValueError:
            name = self.path
        return f"{name}:{self.line}: {self.message}"


def tokenize(source: str) -> list[Token]:
    result = []
    line, end = 1, 0
    for match in TOKEN.finditer(source):
        line += source.count("\n", end, match.start())
        if match.lastgroup != "comment":
            result.append(Token(match[0], match.lastgroup or "symbol", line))
        line += match[0].count("\n")
        end = match.end()
    return result


def split_top(tokens: list[Token], delimiter: str) -> list[list[Token]]:
    groups, current, depth = [], [], 0
    for token in tokens:
        if token.text == delimiter and depth == 0:
            groups.append(current)
            current = []
        else:
            current.append(token)
            if token.text in ("(", "[", "{"):
                depth += 1
            elif token.text in (")", "]", "}"):
                depth -= 1
    groups.append(current)
    return groups


def closing(tokens: list[Token], start: int) -> int | None:
    depth = 0
    for index in range(start, len(tokens)):
        if tokens[index].text in ("(", "[", "{"):
            depth += 1
        elif tokens[index].text in (")", "]", "}"):
            depth -= 1
            if depth == 0:
                return index
    return None


def static_values(tokens: list[Token]) -> tuple[list[tuple[str, int]], bool]:
    """Return known display branches and whether all branches are understood."""
    while tokens and tokens[0].text == "(" and closing(tokens, 0) == len(tokens) - 1:
        tokens = tokens[1:-1]
    if not tokens:
        return [], True
    if len(tokens) == 1:
        value = tokens[0].literal
        return ([(value, tokens[0].line)], True) if value is not None else ([], False)
    # Named arguments do not turn the parameter name into a catalog key.
    if len(tokens) > 2 and tokens[1].text == ":":
        return static_values(tokens[2:])
    joined = split_top(tokens, "+")
    if len(joined) > 1:
        values = [static_values(part) for part in joined]
        if all(known and len(part) == 1 for part, known in values):
            return [("".join(part[0][0] for part, _ in values), tokens[0].line)], True
        return [], False
    coalesced = split_top(tokens, "??")
    if len(coalesced) > 1:
        values = [static_values(part) for part in coalesced]
        return [value for part, _ in values for value in part], all(known for _, known in values)
    conditional = split_top(tokens, "?")
    if len(conditional) == 2:
        branches = split_top(conditional[1], ":")
        if len(branches) == 2:
            values = [static_values(part) for part in branches]
            return [value for part, _ in values for value in part], all(known for _, known in values)
    switch = next((i for i, token in enumerate(tokens) if token.text == "switch"), None)
    if switch is not None and switch + 1 < len(tokens) and tokens[switch + 1].text == "{":
        end = closing(tokens, switch + 1)
        if end == len(tokens) - 1:
            values = []
            for branch in split_top(tokens[switch + 2:end], ","):
                parts = split_top(branch, "=>")
                if len(parts) == 2:
                    values.append(static_values(parts[1]))
            return [value for part, _ in values for value in part], all(known for _, known in values)
    # Literal array / collection expressions used by translated ComboBoxes.
    opening = 0 if tokens[0].text == "[" else next(
        (i for i, token in enumerate(tokens) if token.text == "{" and tokens[0].text == "new"), None)
    if opening is not None and closing(tokens, opening) == len(tokens) - 1:
        values = [static_values(part) for part in split_top(tokens[opening + 1:-1], ",") if part]
        return [value for part, _ in values for value in part], all(known for _, known in values)
    # A wrapper given already-translated text does not introduce another key.
    if len(tokens) > 4 and "".join(t.text for t in tokens[:3]) in GLOBAL_WRAPPERS:
        return [], True
    return [], False


def scan_source(path: Path) -> tuple[list[Finding], list[Finding], list[Finding]]:
    tokens = tokenize(path.read_text(encoding="utf-8-sig"))
    wrapper_file = "MainWindow.cs" if path.name.startswith("MainWindow.") else path.name
    wrappers = GLOBAL_WRAPPERS | LOCAL_WRAPPERS.get(wrapper_file, {})
    keys, dynamic, audit = [], [], []
    for index, token in enumerate(tokens):
        if token.text != "(" or index == 0:
            continue
        name = tokens[index - 1].text
        if index >= 3 and tokens[index - 2].text == ".":
            name = tokens[index - 3].text + "." + name
        end = closing(tokens, index)
        if end is None:
            continue
        arguments = split_top(tokens[index + 1:end], ",")
        if name == "ToolTip.SetTip" and len(arguments) > 1:
            values, known = static_values(arguments[1])
            if known:
                audit.extend(Finding(path, line, "review untranslated tooltip: " + repr(value))
                             for value, line in values if re.search(r"[A-Za-z]", value))
        if name not in wrappers:
            continue
        # Skip declarations rather than reporting wrapper parameters as callers.
        if any(a and a[0].text in ("string", "Window", "StackPanel", "Control") for a in arguments):
            continue
        if name == "Command" and any("".join(t.text for t in a) == "translate:false" for a in arguments):
            continue
        for position in wrappers[name]:
            if position >= len(arguments):
                continue
            if path.name == "DitherDialog.cs" and name == "Choice" and position == 3 and len(arguments) == 5:
                continue  # CheckBox overload: argument 4 is its selected bool.
            expression = arguments[position]
            values, known = static_values(expression)
            keys.extend(Finding(path, line, value) for value, line in values if needs_translation(value))
            if not known:
                dynamic.append(Finding(path, token.line,
                    f"runtime key in {name} argument {position + 1}: " +
                    " ".join(t.text for t in expression)[:120]))
    # Arrays immediately followed by Select(L10n.T); variable collections remain
    # explicit runtime boundaries. No arbitrary string table is translated.
    for index in range(6, len(tokens)):
        if [t.text for t in tokens[index:index + 7]] != [".", "Select", "(", "L10n", ".", "T", ")"]:
            continue
        end = index - 1
        candidates = [i for i in range(end) if tokens[i].text in ("[", "{") and closing(tokens, i) == end]
        if candidates:
            start = candidates[-1]
            parts = split_top(tokens[start + 1:end], ",")
            for part in parts:
                values, known = static_values(part)
                keys.extend(Finding(path, line, value) for value, line in values if needs_translation(value))
                if not known:
                    dynamic.append(Finding(path, tokens[start].line, "runtime value in translated collection"))
        else:
            dynamic.append(Finding(path, tokens[index].line, "runtime collection in Select(L10n.T)"))
    for index in range(len(tokens) - 2):
        if tokens[index].text not in ("Text", "Content", "Header", "Title", "PlaceholderText"):
            continue
        if tokens[index + 1].text != "=":
            continue
        literal = tokens[index + 2]
        if literal.kind == "string" and re.search(r"[A-Za-z]", literal.literal or literal.text):
            audit.append(Finding(path, literal.line, "review raw UI assignment: " + literal.text))
    return keys, dynamic, audit


def needs_translation(value: str) -> bool:
    # Empty labels, punctuation-only icons and numeric ratios are language neutral.
    return any(character.isalpha() for character in value)


def scan_display_enums(path: Path, names: set[str]) -> list[Finding]:
    tokens = tokenize(path.read_text(encoding="utf-8-sig"))
    keys = []
    for index in range(len(tokens) - 2):
        if tokens[index].text != "enum" or tokens[index + 1].text not in names:
            continue
        start = next((i for i in range(index + 2, len(tokens)) if tokens[i].text == "{"), None)
        if start is None:
            continue
        end = closing(tokens, start)
        if end is None:
            continue
        for member in split_top(tokens[start + 1:end], ","):
            while member and member[0].text == "[":
                attribute_end = closing(member, 0)
                member = member[attribute_end + 1:] if attribute_end is not None else []
            if member and member[0].kind == "word":
                keys.append(Finding(path, member[0].line, member[0].text))
    return keys


def format_indices(value: str) -> set[int]:
    """Validate .NET composite braces and return argument indices (not order)."""
    indices, i = set(), 0
    while i < len(value):
        if value[i:i + 2] in ("{{", "}}"):
            i += 2
        elif value[i] == "{":
            match = re.match(r"\{([0-9]+)(?:\s*,\s*-?[0-9]+)?\s*(?::[^{}]*)?\}", value[i:])
            if match is None:
                raise ValueError("malformed composite format at " + repr(value[i:i + 32]))
            indices.add(int(match[1]))
            i += len(match[0])
        elif value[i] == "}":
            raise ValueError("unescaped closing brace")
        else:
            i += 1
    return indices


def read_catalog(directory: Path) -> tuple[dict[str, str], list[Finding]]:
    catalog, owners, errors = {}, {}, []
    files = sorted(directory.glob("*.zh-Hans.json"))
    if not files:
        errors.append(Finding(directory, 1, "no *.zh-Hans.json catalogs found"))
    for path in files:
        source = path.read_text(encoding="utf-8-sig")
        duplicates = []

        def pairs_hook(pairs):
            result = {}
            for key, value in pairs:
                if key in result:
                    duplicates.append(key)
                result[key] = value
            return result

        def key_line(key):
            encoded = json.dumps(key, ensure_ascii=False)
            offset = source.find(encoded)
            if offset < 0:
                offset = source.find(json.dumps(key))
            return source.count("\n", 0, max(offset, 0)) + 1

        try:
            entries = json.loads(source, object_pairs_hook=pairs_hook)
        except json.JSONDecodeError as error:
            errors.append(Finding(path, error.lineno, "invalid JSON: " + error.msg))
            continue
        for key in duplicates:
            errors.append(Finding(path, key_line(key), "duplicate JSON key: " + repr(key)))
        if not isinstance(entries, dict):
            errors.append(Finding(path, 1, "catalog must be a JSON object"))
            continue
        for key, value in entries.items():
            line = key_line(key)
            if not key.strip() or not isinstance(value, str) or not value.strip():
                errors.append(Finding(path, line, "empty key or non-string/empty translation: " + repr(key)))
                continue
            if key in catalog and catalog[key] != value:
                errors.append(Finding(path, line, f"conflicting translation for {key!r}; first in {owners[key].name}"))
            try:
                if format_indices(key) != format_indices(value):
                    errors.append(Finding(path, line, "format argument indices differ: " + repr(key)))
            except ValueError as error:
                errors.append(Finding(path, line, f"{error}: {key!r}"))
            catalog[key], owners[key] = value, path
    return catalog, errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2], help="repository root (default: relative to this script)")
    parser.add_argument("--audit-ui", action="store_true", help="print raw UI assignment/tooltip review hints")
    parser.add_argument("--show-dynamic", action="store_true", help="print runtime keys outside static coverage")
    options = parser.parse_args(argv)
    root = options.root.resolve()
    catalog, errors = read_catalog(root / "windows/src/Compositor.Core/Localization")
    source_files = sorted((root / "windows/src/Compositor.Desktop").rglob("*.cs"))
    source_files = [path for path in source_files if not {"bin", "obj"}.intersection(path.parts)]
    keys, dynamic, audit = [], [], []
    if not source_files:
        errors.append(Finding(root, 1, "no Desktop C# sources found"))
    for path in source_files:
        found, boundaries, hints = scan_source(path)
        keys.extend(found)
        dynamic.extend(boundaries)
        audit.extend(hints)
    for relative, names in DISPLAY_ENUMS.items():
        path = root / "windows/src/Compositor.Core" / relative
        if path.is_file():
            keys.extend(scan_display_enums(path, names))
    errors.extend(Finding(key.path, key.line, "missing zh-Hans translation: " + repr(key.message))
                  for key in keys if key.message not in catalog)
    for finding in sorted(set(errors), key=lambda f: (str(f.path), f.line, f.message)):
        print("ERROR " + finding.display(root))
    if options.audit_ui:
        for finding in audit:
            print("REVIEW " + finding.display(root))
    if options.show_dynamic:
        for finding in dynamic:
            print("DYNAMIC " + finding.display(root))
    print(f"Catalog: {len(catalog)} keys; source: {len(source_files)} Desktop files + listed display enums, "
          f"{len(set(key.message for key in keys))} distinct static keys; "
          f"{len(set(errors))} errors.")
    print(f"Coverage boundary: {len(dynamic)} runtime expressions, {len(audit)} raw UI review hints. "
          "Use --show-dynamic / --audit-ui; runtime UI review is still required.")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
