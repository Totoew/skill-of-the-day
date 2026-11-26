// Regression oracle for the server implementation; not imported by the application.
import { basicSkills, coverage, minimumDeficit, deficit } from "../../src/data.js";

// Bases stay above the specialised steps. The plan is not capped at three skills.
// Each specialised step is evaluated after the missing bases and previous steps.
export function buildPlan(jobs, skills, direction = "analyst", bases = basicSkills[direction]) {
  let context = [...new Set([...skills, ...bases])];
  const steps = [];
  const limit = new Set(jobs.flatMap((job) => job.required)).size;
  for (let index = 0; index < limit; index++) {
    const missing = jobs
      .map((job) => deficit(job, context))
      .filter((item) => item.length);
    if (!missing.length) break;
    const nearest = Math.min(...missing.map((item) => item.length));
    const bundles = [
      ...new Map(
        missing
          .filter((item) => item.length === nearest)
          .map((item) => [[...item].sort().join("\u0000"), item]),
      ).values(),
    ];
    bundles.sort(
      (a, b) =>
        coverage(jobs, [...context, ...b]) - coverage(jobs, [...context, ...a]),
    );
    const chosen = bundles[0].slice(0, 5),
      before = [...context],
      beforeCoverage = coverage(jobs, before);
    context = [...new Set([...context, ...chosen])];
    const afterCoverage = coverage(jobs, context);
    steps.push({
      index,
      skills: chosen,
      before,
      beforeCoverage,
      afterCoverage,
      gain: afterCoverage - beforeCoverage,
      percentagePoints: jobs.length
        ? ((afterCoverage - beforeCoverage) / jobs.length) * 100
        : 0,
      partial: bundles[0].length > 5,
      minimumBefore: nearest,
      minimumAfter: minimumDeficit(jobs, context),
    });
  }
  return steps;
}
