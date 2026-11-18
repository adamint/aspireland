import type { Cohort, CohortState } from "./types";

export const cohortStates = ["outside", "platform", "transfer", "onboard", "completed", "abandoned"] as const satisfies readonly CohortState[];

function isReady(cohort: Cohort, simulatedTime: string): boolean {
  return new Date(cohort.readyAt).getTime() <= new Date(simulatedTime).getTime();
}

export function isWaitingCohort(cohort: Cohort, simulatedTime: string): boolean {
  return isReady(cohort, simulatedTime) && (cohort.state === "outside" || cohort.state === "platform");
}

export function isTransferringCohort(cohort: Cohort): boolean {
  return cohort.state === "transfer";
}

export function summarizeCohorts(cohorts: Cohort[], simulatedTime: string) {
  return cohorts.reduce(
    (summary, cohort) => {
      if (isTransferringCohort(cohort)) {
        summary.transfers += cohort.count;
        return summary;
      }
      if (!isReady(cohort, simulatedTime)) return summary;
      if (cohort.state === "outside") {
        summary.outside += cohort.count;
        summary.waiting += cohort.count;
      } else if (cohort.state === "platform") {
        summary.platform += cohort.count;
        summary.waiting += cohort.count;
      }
      return summary;
    },
    { waiting: 0, outside: 0, platform: 0, transfers: 0 },
  );
}
