# A tiny image for E2EWorktreeTests. CACHEBUST gives every build its own image ID. The COPY gives it a
# file layer: without one, the ID in the --iidfile is one the containerd store cannot find.
FROM scratch
ARG CACHEBUST
LABEL lotrokoniecdev.e2e.probe=$CACHEBUST
COPY tests/LotroKoniecDev.TranslationSystem.E2E.Tests/Probes/tag-probe.dockerfile /probe
