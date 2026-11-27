# Blue-green with Argo CD and Argo Rollouts

This is the "ship it without the change window" part of the demo. The one-command
deploy in the README still works on its own and needs none of this.

## Who does what

| Tool | Job in this demo |
|---|---|
| Argo CD | Makes the cluster match Git. Every change is a commit. |
| Argo Rollouts | Runs the new build beside the old one, tests it, and switches traffic only when it is allowed to. |

Both ship with the OpenShift GitOps operator. There is nothing else to install.

Blue is whatever is live. Green is the build you just committed. In this walk-through
blue is the UBI build and green is the Hardened Images build.

## Two gates

A new build has to pass two gates before every customer is on it.

| Gate | Where | What it is |
|---|---|---|
| **Machine** | both services | A smoke test calls the new build from inside the project. If it fails, the rollout aborts and customers never see it. |
| **Human** | `web` only | After the smoke test passes, the front end waits. A person opens the preview URL, looks, and promotes. |

The pricing service has only the machine gate, so it promotes on its own. The front
end has both. That shows the two styles side by side; in real use you choose per service.

## One-time setup

You need cluster administrator rights for steps 1 and 2.

```bash
# 1. Install the Red Hat OpenShift GitOps operator from OperatorHub, then turn on
#    Argo Rollouts:
oc apply -f gitops/rolloutmanager.yaml

# 2. Create the project, let Argo CD manage it, and generate the database password
#    (it is never stored in Git):
oc new-project acme-insurance
oc label namespace acme-insurance argocd.argoproj.io/managed-by=openshift-gitops
oc create secret generic acme-db -n acme-insurance \
  --from-literal=POSTGRESQL_USER=acme \
  --from-literal=POSTGRESQL_DATABASE=acme \
  --from-literal=POSTGRESQL_PASSWORD="$(openssl rand -hex 16)"

# 3. Point Argo CD at the repository (edit repoURL in the file first if you forked it):
oc apply -f gitops/application.yaml

# 4. Find the two URLs: live, and preview:
oc get route -n acme-insurance -o custom-columns=NAME:.metadata.name,HOST:.spec.host
```

For a nicer view of a rollout, install the CLI plugin (`brew install argoproj/tap/kubectl-argo-rollouts`)
and keep this running in a second terminal:

```bash
oc argo rollouts get rollout web -n acme-insurance --watch
```

## Ship a build

Argo CD watches `deploy/live`. That directory holds one line that names the build to
run. Change the line, commit, push.

```bash
sed -i '' 's#overlays/ubi#overlays/hardened#' deploy/live/kustomization.yaml   # macOS; drop the '' on Linux
git commit -am "Move to the Hardened Images build" && git push
```

What happens next, without anyone touching the cluster:

1. Argo CD sees the commit and syncs it.
2. Argo Rollouts starts the new build **beside** the old one. Customers are still on the old one.
3. The smoke test runs against the new build.
4. The pricing service passes and switches over on its own.
5. The front end passes and **waits**, with the new build reachable at the preview URL.

Then a person decides:

```bash
oc argo rollouts promote web -n acme-insurance     # go
oc argo rollouts abort web -n acme-insurance       # or: no, stay on the old build
```

(Without the plugin, open the `web` Rollout in the Argo CD UI and use its **Resume** action.)

## Roll back

```bash
git revert --no-edit HEAD && git push
```

The old build is kept running for ten minutes after a promotion, so within that window
going back is a traffic switch, not a redeploy. After ten minutes it still works; it
just has to start the old pods again first.

## Talk track

1. **Start on blue.** Open the live URL. "Under the hood" shows UBI on both services,
   with a shell and a package manager in each image.
2. **Commit.** Change the one line and push. Show the commit in Argo CD: an author, a
   message, a one-line diff. That is the whole change record.
3. **Nothing happens to customers.** Refresh the live URL. The front end is still UBI.
   The pricing service card has already changed to Hardened Images: it passed its
   smoke test and switched over, and nobody noticed.
4. **Preview.** Open the preview URL. Same application, both services on Hardened
   Images, no shell, no package manager, running in production before any customer
   sees it.
5. **Promote.** One command. Refresh the live URL: both cards now say Hardened Images.
6. **Roll back.** `git revert`, push. Traffic returns to the old build in seconds
   because it never went away.

The line to land: *when going back takes seconds, you no longer need a change window
to ship a security fix.*

## Things to know before presenting

- **Not tested.** These manifests have not been applied to a cluster. Run the full
  sequence once before showing it to anyone.
- **Check the Rollouts scope.** `gitops/rolloutmanager.yaml` creates the default,
  cluster-wide Argo Rollouts instance in `openshift-gitops`. If the `web` Rollout sits
  with no status after Argo CD syncs, the instance is not watching the project; see
  "Using a cluster-scoped Argo Rollouts instance" in the OpenShift GitOps documentation
  for your version.
- **The first sync is not a rollout.** There is nothing to compare against, so the
  first build goes straight to live with no smoke test and no pause.
- **A rebuilt `-latest` image does not start a rollout.** A rollout starts when the
  manifest in Git changes. In real use the pipeline writes each new image digest into
  Git, and that commit is what ships the fix. Here the switch between the two overlays
  stands in for that commit.
- **The smoke test is deliberately small.** It checks that the new build answers and,
  for the pricing service, that it can reach the database. A real one would exercise a
  quote. Argo Rollouts can also gate on metrics such as error rate from Prometheus.
- **The two services roll out independently.** For a short time the live front end
  (old build) calls the new pricing service. That is harmless here because both builds
  behave the same, and it is worth saying if someone asks.
- **Do not use `oc set image` on this path.** Argo CD would put it back. Git is the
  only way in, which is the point.
- **Secrets.** Creating the database Secret by hand keeps the demo simple. A real
  GitOps setup would use Sealed Secrets or the External Secrets Operator.
