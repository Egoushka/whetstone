# Security policy

## Reporting a vulnerability

Use GitHub private vulnerability reporting on this repository (Security tab → "Report a vulnerability"). Don't open a
public issue. Expect an acknowledgement within 7 days.

## Scope notes

whetstone stores the prompts people write, and prompts often carry private details or pasted secrets. Report any path
where a secret survives redaction, one user's data is readable by another, a deleted prompt survives in a template, or
a rewrite can carry instructions a client would act on without showing them to the user.

## Supported versions

Pre-alpha: only the latest commit on `main`.
