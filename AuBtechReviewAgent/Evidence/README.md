# Evidence

What the review knows about each included study. `DocumentRAGUtility.cs` finds a legal open-access full text, downloads it and splits it into page-tagged chunks. `StudyExtractor.cs` fills the extraction form and the MMAT appraisal for each study, and checks in code that every value comes with a quote that is really in the paper. `RiskOfBias.cs` turns the MMAT answers into the traffic-light view of Table 3.3.
