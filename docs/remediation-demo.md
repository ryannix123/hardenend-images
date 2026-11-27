# Remediation demo: See it. Fix it. Ship it. Prove it.

**Objective:** show a real, known vulnerability leave a running application in
minutes, with two small human decisions and no change window. Then show the
vulnerabilities that never arrive at all with Hardened Images.

Runs on the OpenShift Developer Sandbox: no cluster admin, no operators.

## How it works

| Beat | What the audience sees | What is doing it |
|---|---|---|
| **See it** | The app runs on a base image months old. The panel shows the date; Quay shows the findings | `scripts/time-machine.sh` pinned the UBI base to an older real Red Hat build |
| **Fix it** | A pull request, opened by a bot, with a comment listing exactly which CVEs it removes | Dependabot, plus the pipeline's before-and-after scan |
| **Ship it** | Merge. The new build rolls out without anyone touching the cluster | The ImageStream notices the new digest on Quay; the Deployment rolls out by itself |
| **Prove it** | The panel shows a recent base image date; the findings are gone | The app reads the facts from its own containers |

Then the closer: switch to Hardened Images and the count drops again, because
those packages were never shipped.

## The day before

**1. Deploy** (if you have not already):

```bash
oc process -f deploy/acme-insurance.yaml | oc apply -f -
```

**2. Turn back the clock.** Pick a "before" build for each UBI base. Older builds
carry more already-fixed CVEs. Three to six months is a good start.

```bash
scripts/time-machine.sh list web          # see what exists, with dates
scripts/time-machine.sh pin web 120       # newest build at least 120 days old
scripts/time-machine.sh pin quote-api 120
git commit -am "Demo: start from older base images" && git push
```

The pipeline rebuilds both services on the old bases and pushes them as
`web-ubi-latest` and `quote-api-ubi-latest`. When it finishes:

```bash
scripts/demo.sh pull       # take the old builds now, rather than within 15 minutes
scripts/demo.sh status     # each service should report an old base image date
```

**3. Get the fix waiting.** In GitHub: **Insights > Dependency graph > Dependabot**,
then **Check for updates** on the `services/web` and `services/quote-api` entries.
Dependabot opens a pull request moving each base to the current build. Do not merge.

**4. Check the pull request comment.** The pipeline posts a table: findings in the
live image against findings in the pull request build, and the critical and high
CVEs it removes, by ID. **If it says nothing is fixed, go back further** (step 2 with
a larger number) and try again. The demo must show a real fix, so this is the check
that matters.

**5. Look at Quay.** Open the `web-ubi-latest` tag's security scan. That is the
"see it" screen.

## On the day (8 minutes)

Open the site an hour before. The Sandbox scales idle apps down, and the first
request after that is slow.

### See it (2 minutes)

Open the site. In "Under the hood", read the **Base image built** date out loud:

> "This is running on a base image from August. Nothing is broken. It works. That's
> exactly why it stays running."

Open Quay's scan for the live tag. Pick one critical or high finding.

> "Every one of these already has a fix. The fix is not the hard part. Noticing,
> rebuilding and finding a window is."

### Fix it (2 minutes)

Open Dependabot's pull request.

> "Nobody here went looking for this. A bot noticed Red Hat published a newer build
> and opened a pull request. The pipeline already built it and scanned it."

Scroll to the comment. Read the "removes" list.

> "This is what the change does to what's running, by CVE ID. One line in the diff.
> That's the review."

Merge it. That is human decision number one.

### Ship it (2 minutes)

The pipeline builds and pushes the fixed image (about three minutes; talk over it,
or have a second pull request already merged earlier for a service you do not show).

```bash
scripts/demo.sh pull
```

> "The cluster watches Quay. When the digest moves, it rolls out. Old pods stay up
> until new ones pass their health checks. No change window, no ticket."

Without the `pull`, the cluster picks it up on its own within about 15 minutes.
Say so; it is the point.

### Prove it (2 minutes)

Refresh the site. The base image date is recent. Buy a policy: the application
behaves the same.

Then the closer:

```bash
scripts/demo.sh switch hardened
```

> "That's remediation: the fix arrived and went out. Now look at what happens when
> those packages were never in the image to begin with."

Show the "Same commit, two base images" table from the latest pipeline run.

## Reset for the next run

Repeat "The day before", steps 2 to 4. Dependabot will offer the same fix again.

```bash
scripts/demo.sh switch ubi
```

## Where the Argo path fits

On a cluster where you have admin, "ship it" can be Argo CD plus Argo Rollouts:
the merge becomes a smoke-tested, previewed blue-green rollout, and rollback is
`git revert`. See [gitops-rollouts.md](gitops-rollouts.md). The Sandbox has neither.

## Things to know

- **Not yet run end to end.** The ImageStream setup, the pull request comment and
  the time machine are untested on a cluster and on GitHub. Do a full dry run.
- **`name:tag@digest` in a FROM line.** The time machine writes this form so
  Dependabot can see both the tag and the digest. If the Buildah step rejects it,
  remove the `:tag` part and tell me; Dependabot needs a different setup then.
- **Dependabot and permissions.** The comment job asks for `pull-requests: write`.
  GitHub gives Dependabot's pull requests a read-only token unless the workflow asks
  for more, which this one does. No Quay secrets are needed: pull request builds are
  never pushed, and the live image is read from the public repository.
- **Only the UBI build is turned back.** The hardened build stays current, so its row
  in the pull request comment shows no change.
- **Rollback** is `oc rollout undo deployment/web`, or revert the merge in Git and let
  the pipeline rebuild.
