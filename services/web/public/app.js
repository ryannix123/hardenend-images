(function () {
  "use strict";
  var $ = function (id) { return document.getElementById(id); };
  var money = new Intl.NumberFormat("en-US", { style: "currency", currency: "USD" });
  var current = 1;
  var quote = null;

  function show(step) {
    current = step;
    document.querySelectorAll("[data-panel]").forEach(function (p) {
      p.hidden = Number(p.getAttribute("data-panel")) !== step;
    });
    var marker = Math.min(step, 4);
    document.querySelectorAll("#steps li").forEach(function (li) {
      var n = Number(li.getAttribute("data-step"));
      li.classList.toggle("on", n === marker);
      li.classList.toggle("done", n < marker || step === 5);
    });
    fail("");
  }

  function fail(message) {
    var box = $("error");
    box.textContent = message;
    box.hidden = !message;
  }

  function panelValid(step) {
    var panel = document.querySelector('[data-panel="' + step + '"]');
    var fields = panel.querySelectorAll("input, select");
    for (var i = 0; i < fields.length; i++) {
      if (!fields[i].checkValidity()) { fields[i].reportValidity(); return false; }
    }
    return true;
  }

  function request(path, options) {
    return fetch(path, options).then(function (res) {
      return res.json().catch(function () { return {}; }).then(function (body) {
        if (res.ok) return body;
        var detail = body.errors ? Object.keys(body.errors).map(function (k) { return body.errors[k][0]; }).join(" ") : "";
        throw new Error(detail || body.error || body.title || "Something went wrong. Try again.");
      });
    });
  }

  function post(path, payload) {
    return request(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) });
  }

  function payload() {
    return {
      driver: {
        firstName: $("driver-first").value.trim(),
        lastName: $("driver-last").value.trim(),
        age: Number($("driver-age").value),
        yearsLicensed: Number($("driver-licensed").value),
        incidents: Number($("driver-incidents").value)
      },
      vehicle: {
        year: Number($("vehicle-year").value),
        make: $("vehicle-make").value,
        model: $("vehicle-model").value.trim(),
        annualMiles: Number($("vehicle-miles").value)
      },
      coverage: document.querySelector('input[name="coverage"]:checked').value
    };
  }

  function renderQuote(q) {
    $("monthly").textContent = money.format(q.monthlyPremium);
    $("annual").textContent = money.format(q.annualPremium);
    var body = $("breakdown");
    body.textContent = "";
    q.breakdown.forEach(function (line) {
      var tr = document.createElement("tr");
      var label = document.createElement("td");
      var amount = document.createElement("td");
      label.textContent = line.label;
      amount.textContent = (line.amount < 0 ? "− " : "") + money.format(Math.abs(line.amount));
      if (line.amount < 0) amount.className = "minus";
      tr.appendChild(label); tr.appendChild(amount); body.appendChild(tr);
    });
  }

  document.querySelectorAll("[data-next]").forEach(function (b) {
    b.addEventListener("click", function () { if (panelValid(current)) show(current + 1); });
  });
  document.querySelectorAll("[data-back]").forEach(function (b) {
    b.addEventListener("click", function () { show(current - 1); });
  });

  $("quote-form").addEventListener("submit", function (e) {
    e.preventDefault();
    var button = $("get-quote");
    button.disabled = true;
    post("/api/quotes", payload())
      .then(function (q) { quote = q; renderQuote(q); show(4); })
      .catch(function (err) { fail(err.message); })
      .then(function () { button.disabled = false; });
  });

  $("buy").addEventListener("click", function () {
    var button = $("buy");
    button.disabled = true;
    post("/api/policies", { quoteId: quote.quoteId })
      .then(function (p) {
        $("policy-number").textContent = p.policyNumber;
        $("policy-detail").textContent = "Your " + quote.coverage + " cover starts on " + p.effectiveDate + " at " + money.format(quote.monthlyPremium) + " a month.";
        show(5);
      })
      .catch(function (err) { fail(err.message); })
      .then(function () { button.disabled = false; });
  });

  $("start-over").addEventListener("click", function () { show(1); });
  $("new-quote").addEventListener("click", function () { quote = null; show(1); });

  request("/api/vehicles/makes").then(function (makes) {
    var select = $("vehicle-make");
    select.textContent = "";
    makes.forEach(function (make, i) {
      var option = document.createElement("option");
      option.value = make; option.textContent = make;
      if (i === 2) option.selected = true;
      select.appendChild(option);
    });
  }).catch(function () { fail("The quote service is not reachable. The form will work once it is back."); });

  function row(dl, label, value, cls) {
    var dt = document.createElement("dt"); dt.textContent = label;
    var dd = document.createElement("dd"); dd.textContent = value; if (cls) dd.className = cls;
    dl.appendChild(dt); dl.appendChild(dd);
  }

  function card(info) {
    var el = document.createElement("div"); el.className = "hcard";
    var base = document.createElement("span");
    base.className = "base" + (/hardened/i.test(info.baseImage || "") ? " hardened" : "");
    base.textContent = info.baseImage || "unknown base";
    var h = document.createElement("h3"); h.textContent = info.service;
    var rt = document.createElement("p"); rt.className = "rt"; rt.textContent = info.error ? "Not reachable" : info.runtime;
    el.appendChild(base); el.appendChild(h); el.appendChild(rt);
    if (!info.error) {
      var dl = document.createElement("dl");
      row(dl, "Shell in the image", info.hasShell ? "Yes" : "No", info.hasShell ? "yes" : "no");
      row(dl, "Package manager", info.hasPackageManager ? "Yes" : "No", info.hasPackageManager ? "yes" : "no");
      if (typeof info.runsAsRoot === "boolean") row(dl, "Runs as root", info.runsAsRoot ? "Yes" : "No", info.runsAsRoot ? "yes" : "no");
      el.appendChild(dl);
    }
    return el;
  }

  request("/api/info").then(function (info) {
    var box = $("hood-cards");
    box.textContent = "";
    box.appendChild(card(info.web));
    box.appendChild(card(info.api));
  }).catch(function () { $("hood-cards").textContent = "Service details are not available."; });

  show(1);
})();
