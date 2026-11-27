# Acme Insurance demo

A small car insurance application for showing what changes, and what does not, when
you move a real workload from Red Hat UBI to Red Hat Hardened Images.

A customer picks a vehicle, enters a driver, chooses a level of cover, gets a price
and buys a policy. Every service is built twice from the same commit: once on UBI and
once on Hardened Images. The pipeline scans both and prints one comparison table.

"Acme Insurance" is fictional. The pricing is invented and no real policies are sold.

## What is in the box

```
browser ──> web (Node.js 24) ──> quote-api (.NET 10) ──> acme-db (PostgreSQL 16)
```

| Component | What it does | UBI base | Hardened base |
|---|---|---|---|
| `web` | Serves the site and forwards `/api/*` | `ubi10/nodejs-24-minimal` | `hi/nodejs:24` |
| `quote-api` | Prices quotes and issues policies | `ubi9/dotnet-100-aspnet` | `hi/aspnet-runtime:10.0` |
| `acme-db` | Stores quotes and policies | `rhel9/postgresql-16` | not switched in this demo |

## The point of the demo: one line changes

Each service has two files, `Containerfile` and `Containerfile.hardened`. They are
identical except for the final `FROM` line:

```bash
diff services/web/Containerfile services/web/Containerfile.hardened
```

The pipeline fails if they ever differ by more than that. It is possible because the
services were written so the runtime image never needs a shell:

- `ENTRYPOINT` is in exec form, and there is no entrypoint script.
- The container runs as a numeric non-root user, and nothing in either build runs as root.
- Nothing is installed at run time. The web service has no npm dependencies at all.
- The root filesystem is read-only in OpenShift; `quote-api` gets an `emptyDir` at `/tmp`.

An application that shells out, installs packages at start-up or relies on a debug
shell will need more work than this one did. Say so when you present it.

The site's "Under the hood" panel reads these facts live from the running containers:
base image, whether a shell is present, whether a package manager is present, and
whether the process runs as root.

## Before the first build

I could not pull images or compile .NET where this was written, so check these first:

1. **Hardened .NET tag.** Confirm `registry.access.redhat.com/hi/aspnet-runtime:10.0`
   exists at https://images.redhat.com. The Node.js path, `hi/nodejs:24`, is the one
   already used in the OpenClaw project.
2. **UBI Node.js tag.** Confirm `ubi10/nodejs-24-minimal`. If it does not exist, use
   `ubi10/nodejs-24`.
3. **The .NET code has not been compiled.** Run `dotnet build` in `services/quote-api`
   or let the pipeline do it, and expect to fix small things.
4. **`USER 1001` on the hardened images.** A numeric user should work anywhere, and
   OpenShift assigns its own UID regardless. OpenClaw uses `${CONTAINER_DEFAULT_USER}`
   on `hi/nodejs`; switch to that if you prefer.
5. **Quay repositories.** Create `acme-insurance-web` and `acme-insurance-quote-api`
   under your Quay namespace, and set the `QUAY_USERNAME` and `QUAY_PASSWORD` secrets.

The front end was run and clicked through against a mock API. Nothing else was tested.

## Run it locally with rootless Podman

```bash
podman compose up --build                                            # UBI bases
podman compose -f compose.yaml -f compose.hardened.yaml up --build   # Hardened Images
```

Then open http://localhost:8080.

## Deploy to OpenShift

```bash
oc new-project acme-insurance
cp deploy/db.env.example deploy/db.env              # then change the password
oc create secret generic acme-db --from-env-file=deploy/db.env
oc apply -k deploy/overlays/ubi                     # or deploy/overlays/hardened
oc get route acme-insurance -o jsonpath='{.spec.host}'
```

Switch between the two builds while the audience watches:

```bash
oc apply -k deploy/overlays/hardened
oc rollout status deployment/web deployment/quote-api
```

Refresh the site and the "Under the hood" panel changes from UBI to Hardened Images.

The manifests run under the `restricted-v2` SCC: non-root, no privilege escalation,
all capabilities dropped, seccomp `RuntimeDefault`. Network policies deny everything
by default and allow only router to web, web to quote-api, and quote-api to the database.

## Optional: blue-green with Argo CD and Knative

A second deployment path puts the same two builds behind OpenShift GitOps and
OpenShift Serverless. Argo CD syncs from Git, Knative keeps UBI and Hardened Images
as two revisions, and four one-line commits move traffic from 100% blue to a 0%
preview, a 50/50 split and 100% green. See [docs/gitops-knative.md](docs/gitops-knative.md).

## Keeping base images current

`.github/dependabot.yml` asks Dependabot to watch the base images in the Containerfiles,
the NuGet package and the GitHub Actions, and to open a pull request when one changes.
Merging it runs the pipeline.

Two things make that work:

- **The base images are written out in full.** Dependabot cannot follow a `FROM` built
  from an `ARG`, which is why each service has two Containerfiles in place of one
  parameterised file.
- **Pin the digests once.** With only a tag such as `:24`, Dependabot has nothing to
  report when Red Hat rebuilds the image under the same tag. Run
  `scripts/pin-base-images.sh` (needs skopeo, runs rootless) and commit the result.
  After that, every rebuilt base image arrives as a pull request.

The nightly rebuild in the pipeline stays as a backstop. Neither has been run against
this repository yet, so check Dependabot's first pull request carefully.

## The pipeline

`.github/workflows/build.yml` builds all four images, pushes them to Quay, generates an
SBOM for each with Syft, scans each with Grype, and writes one table to the run summary:

| Service | Base | Critical | High | Medium | Low | Packages | Size (MB) |
|---|---|---:|---:|---:|---:|---:|---:|
| web | ubi | | | | | | |
| web | hardened | | | | | | |
| quote-api | ubi | | | | | | |
| quote-api | hardened | | | | | | |

It also rebuilds every night, so base image fixes arrive without waiting for an
application change. Dated and git tags expire on Quay after 14 days; `latest` and
`hardened-latest` do not.

## Read the numbers carefully

- **A scanner counts only what it can read.** Hardened images often ship without a
  package manager. If a scanner does not fully support them, its number is a floor.
  Compare the Grype result here with what Quay shows, and look at the package count
  from the SBOM as a second signal.
- **Hardened images reduce what surrounds the runtime.** A flaw in .NET or Node.js
  itself still needs a patched image and a rebuild, on either base.
- **This application was built to make the switch easy.** The honest claim is "one line
  differs for an application written this way", not "one line for any application".

## Layout

```
services/web/            Node.js 24 front end, no dependencies
services/quote-api/      .NET 10 minimal API, one dependency (Npgsql)
deploy/db/               PostgreSQL, default-deny network policy
deploy/base/             plain Deployments, Services and Route
deploy/overlays/         ubi and hardened, differing only by image tag
deploy/knative/          Knative Services in four blue-green stages
deploy/live/             the one-line pointer Argo CD watches
gitops/                  Argo CD Application
.github/workflows/       build, push, SBOM, scan, compare
.github/dependabot.yml   base image, NuGet and Actions updates
scripts/                 one-time digest pinning for the base images
docs/demo-script.md      a talk track for a ten-minute demo
docs/gitops-knative.md   setup and talk track for the Argo CD and Knative path
```
