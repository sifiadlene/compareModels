---
title: GitHub issue tracker
description: Tracker destination, publication authority, and engineering skill operations.
---

## Repository and authority

Issues and specs live in GitHub Issues for
[`sifiadlene/compareModels`](https://github.com/sifiadlene/compareModels).
Use the `gh` CLI with an explicit `--repo sifiadlene/compareModels` where
supported; verify the destination before writes.

Obtain user approval before publishing changes, including creating or editing
issues, comments, labels, or pull requests, or pushing commits. Approval covers
only the stated actions; setup approval does not authorize publishing a spec.

## Issue operations

Create with `gh issue create`; read with `gh issue view`; list with
`gh issue list` using bounded results and relevant filters. Use `--body-file`
for multiline content and `--json` for structured reads.

Apply configured labels with `gh issue edit`. Before closing an issue,
publish the approved explanation as a comment, then close it.

Use native GitHub sub-issues and issue dependencies when supported. With
`gh api`, sub-issue linking uses the child's database ID and dependency linking
uses the blocker's database ID, not issue numbers. If unavailable, record
`Part of #<parent>` and `Blocked by: #<number>` relationships in the issue body.
A ticket is unblocked when every blocker is closed.

When a skill says publish to the issue tracker, create a GitHub issue after
approval. When it says fetch the relevant ticket, read the GitHub issue.

## Pull requests as a triage surface

PRs as a request surface: no.

## Wayfinding operations

A map is a GitHub issue labelled `wayfinder:map`; children use
`wayfinder:<type>` labels and native sub-issue links where supported.
Use native dependencies where supported, otherwise the relationships above.
Select unassigned open children with no open blockers in map order, claim
through assignment after approval, and record approved answers before closing
and updating the map. Do not create additional wayfinding labels as part of
this setup.
