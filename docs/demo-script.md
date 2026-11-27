# Ten-minute talk track

The headline to land: **same app, one line changed, smaller target.**

## 1. Set the scene (1 minute)

"This is Acme Insurance. It is a small but real application: a Node.js front end,
a .NET pricing service and a PostgreSQL database, running on OpenShift."

Open the site. Point at the "Under the hood" panel: both services say UBI, both have
a shell and a package manager in the image.

## 2. Buy a policy (2 minutes)

Walk through vehicle, driver, coverage. Get the quote. Buy it. Read the policy number.

"That is the customer's view. Nothing about it is going to change."

## 3. Show the one line (2 minutes)

```bash
diff services/quote-api/Containerfile services/quote-api/Containerfile.hardened
```

"Two files, one line apart: the base image. The pipeline refuses to build if they
drift any further than that."

Be straight about why it is that easy: exec-form entrypoint, no start-up script,
numeric non-root user, nothing installed at run time.

## 4. Switch live (2 minutes)

```bash
scripts/demo.sh switch hardened
```

Refresh. The panel now says Red Hat Hardened Images, no shell, no package manager.
Buy another policy to show the application behaves the same.

Optional: `oc rsh deployment/web` fails because there is no shell. That is the point,
and it is also the cost. Show `oc debug` or an ephemeral debug container as the answer.

## 5. Show the numbers (2 minutes)

Open the latest pipeline run and scroll to the comparison table. Read the High and
Critical columns, then the package counts.

Say the caveat out loud: a scanner counts what it can read, so check more than one
signal before quoting a percentage.

## 6. Close (1 minute)

"Hardened images do not prevent a flaw in the runtime itself. They shrink what is
around it: less to patch, less to triage, less for an attacker to use. The way to find
out what that is worth for you is to try it on one of your own services for a sprint."

## Where this connects

- **Remediation.** To show a real CVE fix arriving as a pull request and rolling out,
  see `remediation-demo.md`.
- **Argo CD and Argo Rollouts.** For a blue-green version of step 4, driven by a Git
  commit, smoke-tested and previewed before customers see it, see `gitops-rollouts.md`.
- **Patch on build.** The nightly rebuild in the pipeline is that pattern.
- **Advanced Cluster Security.** Point ACS at the `acme-insurance` namespace to show
  both variants side by side, and use the network policies as the "fence" example.
- **Event-Driven Ansible.** A security event can trigger the same pipeline in place
  of the nightly schedule.
