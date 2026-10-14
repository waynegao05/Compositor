"""Failure-oriented tests for the repository's dependency-free catalog gate."""

import contextlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import check_localization as checker


class CatalogTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

    def write(self, name, values):
        path = self.root / (name + ".zh-Hans.json")
        path.write_text(values if isinstance(values, str) else json.dumps(values), encoding="utf-8")
        return path

    def errors(self):
        return [finding.message for finding in checker.read_catalog(self.root)[1]]

    def test_duplicate_keys_fail_even_when_values_agree(self):
        self.write("a", '{"Save":"保存", "Save":"保存"}')
        self.assertTrue(any("duplicate JSON key" in error for error in self.errors()))

    def test_conflicting_cross_file_key_fails(self):
        self.write("a", {"Save": "保存"})
        self.write("b", {"Save": "另存"})
        self.assertTrue(any("conflicting translation" in error for error in self.errors()))

    def test_identical_shared_translation_is_allowed(self):
        self.write("a", {"Save": "保存"})
        self.write("b", {"Save": "保存"})
        self.assertEqual([], self.errors())

    def test_empty_or_non_string_values_fail(self):
        self.write("a", {"Save": " ", "Cancel": None, "": "空"})
        self.assertEqual(3, len(self.errors()))

    def test_malformed_json_reports_line(self):
        path = self.write("a", '{\n "Save":\n}')
        errors = checker.read_catalog(self.root)[1]
        self.assertEqual(path, errors[0].path)
        self.assertEqual(3, errors[0].line)

    def test_invalid_root_shape_is_an_error(self):
        self.write("a", '["Save"]')
        self.assertIn("catalog must be a JSON object", self.errors())

    def test_missing_catalog_is_an_error(self):
        self.assertIn("no *.zh-Hans.json catalogs found", self.errors())

    def test_format_argument_loss_is_an_error(self):
        self.write("a", {"Copied {0} by {1}": "已复制 {0}"})
        self.assertTrue(any("format argument indices differ" in error for error in self.errors()))

    def test_malformed_format_is_an_error(self):
        self.write("a", {"Saved {0}": "保存 {0"})
        self.assertTrue(any("malformed composite format" in error for error in self.errors()))

    def test_format_reordering_alignment_and_escaped_braces(self):
        self.write("a", {"{{Range}} {0,8:0.00}: {1}": "{1}：{{范围}} {0,-8:0.00}"})
        self.assertEqual([], self.errors())
        self.assertEqual({0, 1}, checker.format_indices("{{Range}} {0,8:0.00}: {1}"))
        with self.assertRaises(ValueError):
            checker.format_indices("value }")


class SourceTests(unittest.TestCase):
    def scan(self, source, filename="MainWindow.cs"):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / filename
            path.write_text(source, encoding="utf-8")
            return checker.scan_source(path)

    def keys(self, source, filename="MainWindow.cs"):
        return {finding.message for finding in self.scan(source, filename)[0]}

    def test_comment_strings_and_command_ids_are_ignored(self):
        source = '''// L10n.T("Not a key")
        /* Command("Not a key", action); */
        Command("Save", action, "stable-command-id");
        TextPrompt.Ask(this, "Title", "Label", "user supplied text");
        Command("user.comp", action, translate: false);
        var path = "a-file.png";
        '''
        self.assertEqual({"Save", "Title", "Label"}, self.keys(source))

    def test_scanner_decodes_csharp_literals_and_reports_line(self):
        source = '\nL10n.T("Save\\n\\u4e2d\\t\\\"File\\\"");\nL10n.T(@"File ""Name""");'
        keys, _, _ = self.scan(source)
        self.assertEqual(['Save\n中\t"File"', 'File "Name"'], [key.message for key in keys])
        self.assertEqual([2, 3], [key.line for key in keys])

    def test_constant_concatenation_is_one_key(self):
        self.assertEqual({"Save project"}, self.keys('L10n.T("Save " + "project");'))

    def test_conditional_and_switch_display_branches(self):
        source = '''L10n.T(enabled ? "On" : "Off");
        L10n.T(mode switch { Mode.A => "First", Mode.B => "Second", _ => "Other" });
        L10n.T(problem ?? "Ready");'''
        self.assertEqual({"On", "Off", "First", "Second", "Other", "Ready"}, self.keys(source))

    def test_translated_inline_collections(self):
        source = 'var a = new[] { "Paint", "Erase" }.Select(L10n.T); Choice("Mode", box, ["Auto", "Custom"]);'
        self.assertEqual({"Paint", "Erase", "Mode", "Auto", "Custom"}, self.keys(source, "CameraRawPanel.cs"))

    def test_wrappers_are_scoped_to_their_own_file(self):
        source = 'Add(panel, "Exposure", 0, 10); Add(collection, "internal-value", 0);'
        self.assertEqual(set(), self.keys(source, "Other.cs"))
        self.assertEqual({"Exposure"}, self.keys('Add(panel, "Exposure", 0, 10);', "FilterDialog.cs"))

    def test_main_window_partial_files_keep_status_translation_checks(self):
        source = 'Say("Recovery failed"); Command("Recover", action, "stable-id");'
        self.assertEqual({"Recovery failed", "Recover"}, self.keys(source, "MainWindow.Recovery.cs"))
        self.assertEqual(set(), self.keys(source, "Other.Recovery.cs"))

    def test_dynamic_expressions_are_visible_without_treating_ids_as_keys(self):
        source = 'L10n.T(lookup("internal-id")); L10n.T($"Size {size}"); L10n.T("""raw key""");'
        keys, dynamic, _ = self.scan(source)
        self.assertEqual([], keys)
        self.assertEqual(3, len(dynamic))

    def test_nested_translation_does_not_turn_format_arguments_into_keys(self):
        self.assertEqual({"Saved {0}"}, self.keys('Say(L10n.F("Saved {0}", "user filename"));'))

    def test_numeric_and_symbol_only_labels_need_no_translation(self):
        self.assertEqual(set(), self.keys('L10n.T(""); ViewButton("100%", "＋", action);'))

    def test_raw_ui_assignments_and_tooltips_get_review_hints(self):
        source = 'Content = "Save"; Text = "123"; Title = L10n.T("Title"); ToolTip.SetTip(button, "Save file");'
        _, _, audit = self.scan(source)
        self.assertEqual(2, len(audit))
        self.assertTrue(any("tooltip" in hint.message for hint in audit))

    def test_display_enum_checks_names_without_translating_storage_attributes(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "Enums.cs"
            path.write_text('''enum Axis {
                [JsonStringEnumMemberName("stored-id")] Horizontal = 1,
                Vertical,
            }
            enum NotDisplayed { InternalName }
            ''', encoding="utf-8")
            found = checker.scan_display_enums(path, {"Axis"})
            self.assertEqual(["Horizontal", "Vertical"], [key.message for key in found])

    def test_missing_key_fails_cli_and_can_be_located(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            catalogs = root / "windows/src/Compositor.Core/Localization"
            desktop = root / "windows/src/Compositor.Desktop"
            catalogs.mkdir(parents=True)
            desktop.mkdir(parents=True)
            (catalogs / "test.zh-Hans.json").write_text('{"Save":"保存"}', encoding="utf-8")
            (desktop / "MainWindow.cs").write_text('\nL10n.T("Missing");', encoding="utf-8")
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                result = checker.main(["--root", str(root)])
            self.assertEqual(1, result)
            self.assertIn("MainWindow.cs:2: missing zh-Hans translation: 'Missing'", output.getvalue())

    def test_default_root_works_outside_the_repo(self):
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.run([sys.executable, str(Path(checker.__file__).resolve())],
                                    cwd=directory, text=True, capture_output=True, check=False)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn("0 errors", result.stdout)


if __name__ == "__main__":
    unittest.main()
