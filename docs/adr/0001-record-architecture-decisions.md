# Record architecture decisions

## Context and Problem Statement

We need to record the architectural decisions made on this project.
Not doing so only works for small projects and puts the project at risk of poor decisions.

## Decision Drivers

* Need to support the learning of new team members and open source contributors
* Need to be able to look back on decisions and understand the reasons behind them
* Need to be able to challenge decisions with new context

## Considered Options

* Don't document architectural decisions
* Document decisions in a central document
* Record architecture decisions in ADR format (MADR) per decision and store them in the repository

## Decision Outcome

Chosen option: "Record architecture decisions in ADR format (MADR) per decision and store them in the repository", because MADR is a well-established format that has proven to work well for recording architectural decisions, it allows easy linking and reference, and it keeps the architecture decisions close to the code.

### Consequences

* Good, because architectural decisions can be referenced, searched for and discussed
* Good, because decisions are stored in version control together with the code
* Good, because new team members can understand the architectural decisions and the reasons behind them
* Good, because the decision format is easy to understand and follow
* Good, because decisions can be superseded by new decisions that link back to old ones
* Bad, because it requires team discipline to document decisions
* Bad, because it may take time to find the decision you are looking for
