import { buildPlan } from "./data";
self.onmessage = ({ data }) => {
  try {
    self.postMessage({
      steps: buildPlan(data.jobs, data.skills, data.direction, data.bases),
    });
  } catch {
    self.postMessage({
      error: "Не удалось пересчитать план. Попробуйте ещё раз.",
    });
  }
};
