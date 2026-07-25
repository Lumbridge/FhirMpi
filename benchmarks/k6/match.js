import http from "k6/http";
import { check } from "k6";

const rate = Number(__ENV.RATE || 250);
const duration = __ENV.DURATION || "30m";

export const options = {
  scenarios: {
    match: {
      executor: "constant-arrival-rate",
      rate,
      timeUnit: "1s",
      duration,
      preAllocatedVUs: Number(__ENV.PREALLOCATED_VUS || 300),
      maxVUs: Number(__ENV.MAX_VUS || 1000),
    },
  },
  thresholds: {
    http_req_duration: ["p(95)<250"],
    http_req_failed: ["rate<0.001"],
  },
};

const body = JSON.stringify({
  resourceType: "Parameters",
  parameter: [
    {
      name: "resource",
      resource: {
        resourceType: "Patient",
        identifier: [
          {
            system: "https://fhir.nhs.uk/Id/nhs-number",
            value: "9434765919",
          },
        ],
        name: [{ family: "Synthetic", given: ["Load"] }],
        birthDate: "1980-01-02",
        address: [{ postalCode: "SW1A 2AA" }],
      },
    },
    { name: "onlyCertainMatches", valueBoolean: false },
    { name: "count", valueInteger: 10 },
  ],
});

export default function () {
  const headers = {
    "Content-Type": "application/fhir+json",
    Accept: "application/fhir+json",
  };
  if (__ENV.ACCESS_TOKEN) {
    headers.Authorization = `Bearer ${__ENV.ACCESS_TOKEN}`;
  }

  const response = http.post(
    `${__ENV.BASE_URL || "http://localhost:8080"}/fhir/R4/Patient/$match`,
    body,
    { headers, tags: { endpoint: "patient-match" } },
  );
  check(response, {
    "$match returned 200": (result) => result.status === 200,
  });
}
