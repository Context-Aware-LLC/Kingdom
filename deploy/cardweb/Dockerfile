# Emberfall web card authoring tool.
#
# Build context is the repo ROOT (it needs tooling/cardweb + the card data), the same as
# deploy/walking-skeleton/Dockerfile.server.
#
# THE THUMBNAIL CACHE MUST BE BUILT BEFORE THIS RUNS:
#     python tooling/cardweb/build_thumbs.py
# It is gitignored (a 4.2 MB derived artifact), so it is not in a fresh clone. Copying it in
# rather than generating it here is deliberate: generating it would mean putting 518 MB of
# source art into the build context to produce 4.2 MB of output.

FROM python:3.13-slim

# No Pillow, no third-party anything -- the server is stdlib only. Pillow is a build-time
# dependency of the thumbnails, which are baked in above rather than made here.
WORKDIR /app

# The tool itself.
COPY tooling/cardweb/ /app/tooling/cardweb/

# corpus.py imports art_file() from here rather than re-deriving the art filename, because
# re-deriving it is the bug that stranded 18 hand-authored seeds (see that function's docstring).
# Honouring "import it, do not re-derive it" means the image has to carry the module — the
# alternative is a copied one-liner that drifts, which is exactly what the rule forbids.
# It is stdlib-only at import time and has a __main__ guard, so importing costs nothing.
COPY tooling/scripts/gen_card_prompt_briefs.py /app/tooling/scripts/

# The corpus. Three files; the 518 MB art tree is deliberately NOT shipped -- the server
# resolves art through the thumbnail cache and never reads a source PNG.
COPY unity/Emberfall/Assets/CardData/*.cards.json /app/carddata/

ENV EMBERFALL_CARD_DIR=/app/carddata \
    EMBERFALL_ART_DIR=/app/carddata \
    EMBERFALL_THUMB_DIR=/app/tooling/cardweb/.thumbs \
    EMBERFALL_STAGING_DIR=/data/staging \
    PYTHONUNBUFFERED=1

# /data is a Fly volume. Staged edits are an author's unsaved work and must survive the
# machine suspending, which scale-to-zero makes routine rather than exceptional.
VOLUME ["/data"]

EXPOSE 8080

# CARDWEB_PASSWORD comes from `fly secrets`. Without it the server refuses to bind 0.0.0.0
# at all, so a missing secret fails the deploy loudly instead of publishing the corpus.
CMD ["python", "tooling/cardweb/server.py", "--host", "0.0.0.0", "--port", "8080"]
