# Windows Developer Environment Management

WDEM describes and converges a Windows developer workstation through trusted declarative Profiles and their Task workflows.

## Language

**Version requirement**:
A Profile Task rule that determines whether a detected local product version is acceptable. A lower-bound expression such as `>= 2.50` defines the Task's minimum version.
_Avoid_: Target version, installed version

**Upgrade required**:
A compliance result meaning the Task was detected locally, but its version is below the Profile's declared minimum.
_Avoid_: Missing, generic mismatch

**Task lifecycle event**:
An immutable fact that a Task workflow started, entered a state, started or completed an Activity, transitioned, or finished. It records what happened and never requests work.
_Avoid_: UI event, progress notification, command
