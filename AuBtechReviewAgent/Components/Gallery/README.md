# Gallery

The public gallery of example reports (`/gallery`). `GalleryOffer.razor` is the owner's offer on a finished run's report page: the consent text, the yes, and withdrawing again. `GalleryText.cs` holds the consent text and the rule for duplicates that more than one page shows. The pages are `Components/Pages/Gallery.razor` (the catalogue) and `GalleryReport.razor` (one report, drawn by the report page from the entry's copy); what is kept, and the rules for adding, replacing, capping, hiding and withdrawing, are in `Runs/GalleryStore.cs`.
