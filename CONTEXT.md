# Windows Developer Environment Management

WDEM describes and converges a Windows developer workstation through trusted declarative Profiles and their Task workflows.

WDEM currently contains one bounded context: **Environment Convergence**. Its language ends at the desired Profile, observed compliance, execution Plan, Task Workflow, and outcome; transport, process launching, and presentation are outside the domain boundary.

## Language

**Environment Profile**:
The aggregate root that declares one desired developer environment and owns its complete, valid Task dependency graph.
_Avoid_: Configuration file, package list

**Task**:
A named unit of desired environment capability inside an Environment Profile and the only unit scheduled in its dependency graph.
_Avoid_: Installer, product provider

**Environment convergence**:
The process of comparing a workstation with a trusted Environment Profile and applying its Task workflows until the declared requirements are satisfied or a terminal failure occurs.
_Avoid_: Package installation, setup script

**Version requirement**:
A Profile Task rule that determines whether a detected local product version is acceptable. A lower-bound expression such as `>= 2.50` defines the Task's minimum version.
_Avoid_: Target version, installed version

**Upgrade required**:
A compliance result meaning the Task was detected locally, but its version is below the Profile's declared minimum.
_Avoid_: Missing, generic mismatch

**Task lifecycle event**:
An immutable fact that a Task workflow started, entered a state, started or completed an Activity, transitioned, or finished. It records what happened and never requests work.
_Avoid_: UI event, progress notification, command
