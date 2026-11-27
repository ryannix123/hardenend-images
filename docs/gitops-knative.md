# Blue-green with Argo CD and Knative

This is an optional second way to run the demo. The plain Deployment path in the
README still works on its own and needs neither Serverless nor GitOps.

## Who does what

| Tool | Job in this demo |
|---|---|
| Argo CD (OpenShift GitOps) | Makes the cluster match Git. Every change is a commit. |
| Knative Serving (OpenShift Serverless) | Keeps each deployment as an immutable revision and splits traffic between revisions. |

They do not overlap. Argo CD decides *what* should be running; Knative decides *how
traffic reaches it*. The tool that would overlap with Knative here is Argo Rollouts,
which this demo deliberately leaves out.

Blue is the UBI build. Green is the Hardened Images build.

## One-time setup

Prerequisites: the OpenShift GitOps and OpenShift Serverless operators, with a
`KnativeServing` instance created.

```bash
# 1. Namespace and database secret (the password is never stored in Git)
oc new-project acme-insurance
oc label namespace acme-insurance argocd.argoproj.io/managed-by=openshift-gitops
cp deploy/db.env.example deploy/db.env        # then change the password
oc create secret generic acme-db --from-env-file=deploy/db.env -n acme-insurance

# 2. Point Argo CD at your copy of this repository
#    (edit repoURL in gitops/application.yaml first)
oc apply -f gitops/application.yaml

# 3. Find the site
oc get ksvc web -n acme-insurance -o jsonpath='{.status.url}'
```

## The rollout, one commit at a time

Argo CD watches `deploy/live`. That directory holds one line that names the current
stage. To move the rollout, change that line, commit and push.

| Stage | What is true afterwards |
|---|---|
| `1-blue` | Everything runs on UBI. |
| `2-preview` | The hardened revision is running with **0%** of traffic and its own "green" URL. |
| `3-split` | 50% of traffic goes to each revision. |
| `4-green` | 100% on Hardened Images. Blue is still there for rollback. |

```bash
# Move to the next stage
sed -i 's#stages/1-blue#stages/2-preview#' deploy/live/kustomization.yaml
git commit -am "Preview the hardened build at 0% traffic" && git push
```

(On macOS use `sed -i ''`.)

## Talk track

1. **Start on blue.** Open the site. "Under the hood" shows UBI, with a shell and a
   package manager in the image.
2. **Preview green.** Push stage 2. Nothing changes for customers. Show the tagged URL:
   ```bash
   oc get ksvc web -n acme-insurance -o jsonpath='{range .status.traffic[*]}{.tag}{"  "}{.percent}{"%  "}{.url}{"\n"}{end}'
   ```
   Open the green URL. Same application, now on Hardened Images, tested before any
   customer sees it.
3. **Split.** Push stage 3. Refresh the main URL a few times and watch the panel flip
   between UBI and Hardened Images.
4. **Go green.** Push stage 4.
5. **Roll back.** `git revert HEAD && git push`. Traffic returns to blue in seconds
   because the old revision never went away. Rollback is a traffic change, not a
   redeploy.

Show the Argo CD application view alongside: each step is a commit with an author,
a message and a diff.

## Things to know before presenting

- **Not tested.** These manifests have not been applied to a cluster. Try the full
  sequence once before showing it to anyone.
- **Named revisions are immutable.** `web-ubi` and `web-hardened` are fixed names, and
  Knative resolves the image tag to a digest when the revision is first created. A
  nightly rebuild does not change a running revision. In real use, name each revision
  after the commit or digest and let the pipeline write that into Git.
- **The front end and the pricing service split independently.** During stage 3 a
  green front end can call a blue pricing service. That is harmless here because both
  builds behave the same, and worth saying if someone asks.
- **`emptyDir` on `quote-api`.** The service mounts an `emptyDir` at `/tmp` so its root
  filesystem can stay read-only. Confirm your Serverless version allows `emptyDir`
  volumes on Knative Services; if not, remove the volume and the read-only setting.
- **Network policy.** The Knative path allows ingress from namespaces labelled
  `knative.openshift.io/part-of=openshift-serverless`, which the operator sets on its
  system namespaces.
- **Secrets.** Creating the database Secret by hand keeps the demo simple. A real
  GitOps setup would use Sealed Secrets or the External Secrets Operator.
- **Scale to zero is off.** Each service keeps at least one pod so there are no cold
  starts mid-demo. Lower `min-scale` to `"0"` if you want to show scale to zero.
