# Architecture Checks

Architecture checks turn durable Wiki constraints into executable dependency/API-shape/build rules where automation has clear value.

They must not become a second architecture source. If a rule changes, update the owning Wiki concept first, then update the check.

Checks that only diagnose programmer mistakes during development must not force equivalent shipping-path validation.
