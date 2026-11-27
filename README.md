# Acme Insurance: smaller target, faster fix

**When the next critical CVE lands, two questions decide your week: how much of what
you run is exposed, and how quickly can you safely ship the fix?**

This repository is a working answer to both. It is a small car insurance application
(get a quote, buy a policy) built twice from the same commit: once on Red Hat UBI and
once on Red Hat Hardened Images. You can deploy it in one command, switch between the
two while people watch, and follow a base image fix from pull request to production.

"Acme Insurance" is fictional. The pricing is invented and no real policies are sold.

## The problem

Most of what a scanner flags in a container is not your code. It is everything that
came along with it: a shell, a package manager, utilities the application never calls.
Each of those is something to scan, triage, explain to an auditor and patch.

So a CVE week usually goes like this:

1. A scanner lights up across dozens of images.
2. Teams spend days working out which findings matter.
3. The fix exists upstream, but it waits for someone to notice, rebuild and schedule a release.
4. The release is a big-bang cutover, so it waits for a change window.

The exposure is not the hard part. The waiting is.

## Three moves

| | Move | What does it | What changes for you |
|---|---|---|---|
| 1 | **Shrink** the target | Red Hat Hardened Images | Fewer packages in the image, so fewer findings to triage and less for an attacker to use |
| 2 | **Know** the moment a fix exists | Dependabot | The fix arrives as a pull request, already built and scanned, instead of as a ticket |
| 3 | **Ship** it without the change window | Argo CD and Argo Rollouts | The fix is tested and previewed beside the live version, switched in, and rolled back in seconds if needed |

### 1. Shrink: Hardened Images

A hardened image carries the runtime and little else. No shell. No package manager.

That matters twice. There is **less to patch**, because a package that is not in the
image cannot have a CVE filed against your workload. And there is **less to use**,
because an attacker who finds a way in has no shell to run and no package manager to
pull tools with.

In this demo the cost of getting there is one line. Each service has two files,
`Containerfile` and `Containerfile.hardened`, identical except for the final `FROM`:

```bash
diff services/web/Containerfile services/web/Containerfile.hardened
```

The pipeline fails if they ever differ by more than that. Same code, same build, same
tests, different base.

You do not have to take the claim on trust. The site's "Under the hood" panel reads
the facts live from the running containers: which base, whether a shell is present,
whether a package manager is present, whether the process runs as root. Switch builds
and watch the answers change.

### 2. Know: Dependabot

A smaller image still needs fixes. The question is how you find out.

Dependabot watches the base images, the NuGet package and the pipeline's own actions.
When Red Hat publishes a rebuilt base image, Dependabot opens a pull request. The
pipeline builds it, generates an SBOM and scans it before anyone merges. The person
approving sees one small diff and a scan result, not a spreadsheet of findings.

Nobody has to be watching a security feed. Nobody has to remember to rebuild. A
nightly rebuild runs as a backstop.

### 3. Ship: Argo CD and Argo Rollouts

A fix that is built but not deployed protects nobody. This is where most of the
waiting happens, because a normal release replaces the running version and hopes.

Here, Argo CD makes the cluster match Git, so every change is a commit with an author
and a diff. Argo Rollouts runs the new build beside the old one and moves customers
only when it is allowed to. Both ship with OpenShift GitOps.

| Step | What customers see | What happens |
|---|---|---|
| Commit | Nothing | The new build starts beside the live one |
| Test | Nothing | A smoke test runs against the new build. If it fails, the rollout stops here |
| Preview | Nothing | The new build is reachable at its own URL, in production, with real dependencies |
| Promote | The new build | One command, or automatic once the test passes. The old build keeps running |
| Roll back | The old build, in seconds | `git revert`. It is a traffic switch, not a redeploy |

When rollback takes seconds and costs nothing, you stop needing a change window to
ship a security fix.

Setup and a talk track are in [docs/gitops-rollouts.md](docs/gitops-rollouts.md).

## What a CVE looks like with all three

1. Red Hat publishes a rebuilt base image with the fix.
2. Dependabot opens a pull request the next morning. The pipeline has already built
   and scanned it.
3. A person reads a one-line diff and a clean scan, and merges.
4. That commit starts a rollout. The new build is smoke-tested and previewed beside
   the live one, then promoted.
5. If anything looks wrong, one `git revert` puts traffic back.

Two human decisions, merge and promote, both small. The rest is automation.

If the flaw is in a package the hardened image never shipped, there is no step 1.
Nothing to fix is the fastest fix there is.

## See it in five minutes

One file, one command. It deploys the front end, the pricing service and PostgreSQL
into whatever project you are logged in to. There is nothing to edit, no project name
to pass, and the database password is generated for you.

```bash
oc process -f deploy/acme-insurance.yaml | oc apply -f -
```

No clone needed either; point it at the file on GitHub:

```bash
oc process -f https://raw.githubusercontent.com/ryannix123/hardenend-images/main/deploy/acme-insurance.yaml | oc apply -f -
```

Then open it:

```bash
oc get route acme-insurance -o jsonpath='https://{.spec.host}{"\n"}'
```

The first start takes a minute or so: the pricing service waits for the database,
then creates its own tables.

Switch between the two builds while the audience watches:

To Hardened Images:

```bash
oc set image deployment/web web=quay.io/ryan_nix/acme-insurance:web-hardened-latest
oc set image deployment/quote-api quote-api=quay.io/ryan_nix/acme-insurance:quote-api-hardened-latest
oc rollout status deployment/web deployment/quote-api
```

Back to UBI:

```bash
oc set image deployment/web web=quay.io/ryan_nix/acme-insurance:web-ubi-latest
oc set image deployment/quote-api quote-api=quay.io/ryan_nix/acme-insurance:quote-api-ubi-latest
oc rollout status deployment/web deployment/quote-api
```

Refresh the site and the "Under the hood" panel changes from UBI to Hardened Images.
To start on Hardened Images instead, add `-p BASE=hardened` to `oc process`.

Remove everything:

```bash
oc delete all,secret,pvc,networkpolicy -l app.kubernetes.io/part-of=acme-insurance
```

Good to know:

- **Run the deploy once.** Each run generates a new database password. If you run it
  again, restart the two pods that use it:
  `oc rollout restart deployment/acme-db deployment/quote-api`.
- **The Quay repository must be public**, or the project needs a pull secret for it.
- **It runs under the `restricted-v2` SCC**: non-root, no privilege escalation, all
  capabilities dropped, seccomp `RuntimeDefault`. No cluster-admin rights are needed,
  only edit rights in the project.
- **Network policies deny everything by default** and allow only router to web, web to
  quote-api, and quote-api to the database.

`deploy/acme-insurance.yaml` is generated from `deploy/db` and `deploy/base` by
`scripts/build-template.py`. The kustomize overlays (`oc apply -k deploy/overlays/ubi`)
still work if you prefer them, but they need the `acme-db` Secret created first from
`deploy/db.env.example`.

### Run it locally with rootless Podman

```bash
podman compose up --build                                            # UBI bases
podman compose -f compose.yaml -f compose.hardened.yaml up --build   # Hardened Images
```

Then open http://localhost:8080.


## What is in the box

```
browser ──> web (Node.js 24) ──> quote-api (.NET 10) ──> acme-db (PostgreSQL 16)
```

| Component | What it does | UBI base | Hardened base |
|---|---|---|---|
| `web` | Serves the site and forwards `/api/*` | `ubi10/nodejs-24-minimal` | `hi/nodejs:24` |
| `quote-api` | Prices quotes and issues policies | `ubi9/dotnet-100-aspnet` | `hi/aspnet-runtime:10.0` |
| `acme-db` | Stores quotes and policies | `rhel9/postgresql-16` | not switched in this demo |

### Why one line was enough

The services were written so the runtime image never needs a shell:

- `ENTRYPOINT` is in exec form, and there is no entrypoint script.
- The container runs as a numeric non-root user, and nothing in either build runs as root.
- Nothing is installed at run time. The web service has no npm dependencies at all.
- The root filesystem is read-only in OpenShift; `quote-api` gets an `emptyDir` at `/tmp`.

These are good habits on any base image. They are also the checklist for whether your
own application is ready to move.

### The pipeline

`.github/workflows/build.yml` builds all four images with rootless Buildah, pushes them
to one Quay repository, generates an SBOM for each with Syft, scans each with Grype,
and writes a table to the run summary: critical, high, medium and low findings, package
count and size, UBI beside hardened, for the same commit. Open the latest run under
**Actions** to see the current numbers.

Images are told apart by tag: `web-ubi-latest`, `web-hardened-latest`,
`quote-api-ubi-latest` and `quote-api-hardened-latest`. Dated and git tags expire on
Quay after 14 days; the four `-latest` tags do not.

### Dependabot setup

`.github/dependabot.yml` is already in place. Two things make it work:

- **The base images are written out in full.** Dependabot cannot follow a `FROM` built
  from an `ARG`, which is why each service has two Containerfiles in place of one
  parameterised file.
- **Pin the digests once.** With only a tag such as `:24`, Dependabot has nothing to
  report when Red Hat rebuilds the image under the same tag. Run
  `scripts/pin-base-images.sh` (needs skopeo, runs rootless) and commit the result.
  After that, every rebuilt base image arrives as a pull request.

To build your own copy, create one public Quay repository named `acme-insurance` and
set the `QUAY_USERNAME` and `QUAY_PASSWORD` secrets in GitHub.

## What this demo does not claim

A demo that oversells gets found out in the first technical question. So:

- **Hardened images reduce what surrounds the runtime, not the runtime.** A flaw in
  .NET or Node.js itself still needs a patched image and a rebuild, on either base.
  That is what moves 2 and 3 are for.
- **A scanner counts only what it can read.** Hardened images ship without a package
  manager, and a scanner that does not fully support them may under-report. Treat a
  low number as a floor, and use the SBOM package count as a second signal.
- **One line was enough for this application.** One that shells out, installs packages
  at start-up or relies on a debug shell will need more work first.
- **A rollout starts from a commit.** In real use the pipeline would write each new
  image digest into Git, and that commit ships the fix. Here, switching between the
  UBI and hardened overlays stands in for it.
- **The database stays on its standard image** in this demo.
- **Tested so far:** the UBI builds and the one-command deploy. The switch to the
  hardened builds, the Argo CD and Argo Rollouts path and the Dependabot configuration have
  not yet been confirmed end to end. Walk through them once before presenting.

## Layout

```
services/web/            Node.js 24 front end, no dependencies
services/quote-api/      .NET 10 minimal API, one dependency (Npgsql)
deploy/acme-insurance.yaml   the whole app in one file: start here
deploy/db/               PostgreSQL, default-deny network policy
deploy/base/             plain Deployments, Services and Route
deploy/overlays/         ubi and hardened, differing only by image tag
deploy/rollouts/         the same app as Argo Rollouts: blue-green, smoke test, preview
deploy/live/             the one-line pointer Argo CD watches
gitops/                  Argo CD Application and the RolloutManager
.github/workflows/       build, push, SBOM, scan, compare
.github/dependabot.yml   base image, NuGet and Actions updates
scripts/                 digest pinning, and the one-file deploy generator
docs/demo-script.md      a talk track for a ten-minute demo
docs/gitops-rollouts.md  setup and talk track for the Argo CD and Argo Rollouts path
```
