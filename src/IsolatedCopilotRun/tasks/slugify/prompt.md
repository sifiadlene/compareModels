Implement `slugify(text)` in `/workspace/slugify.py`.

Requirements:
- Return a lowercase string.
- Convert accented Latin letters to their ASCII base letter (for example "é" becomes "e").
- Replace every run of characters that are not ASCII letters or digits with a single hyphen.
- Remove leading and trailing hyphens.
- Return an empty string when nothing remains.

Edit files only under `/workspace`. Use the `run_command` tool to run
`python3 -m unittest discover -s checks -v` from `/workspace` and make the tests pass.
Do not modify files under `checks/`.
